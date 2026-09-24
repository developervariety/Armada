namespace Armada.Runtimes
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.IO;
    using System.Net.Http;
    using System.Text;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Core.Models;
    using Armada.Core.Enums;
    using Armada.Core.Services;
    using Armada.Runtimes.Interfaces;
    using Armada.Runtimes.Mcp;
    using Armada.Runtimes.Tools;
    using Armada.Runtimes.Tools.Tasks;
    using PolyPrompt.Clients;
    using PolyPrompt.Models;
    using SyslogLogging;
    using ArmadaToolDefinition = Armada.Runtimes.Tools.ToolDefinition;
    using PolyToolDefinition = PolyPrompt.Models.ToolDefinition;

    /// <summary>
    /// An agent runtime that drives a configured inference <see cref="ModelEndpoint"/> as a captain. Instead of
    /// spawning a CLI harness, it runs an in-process tool-calling loop (via PolyPrompt) over the Armada
    /// built-in coding tools, executing every tool against the mission's working directory. It surfaces the
    /// same <see cref="IAgentRuntime"/> events as a process-backed runtime (started, output, exited), so the
    /// agent lifecycle handler treats it identically. Processless: it uses a synthetic process id and
    /// cooperative cancellation keyed by that id.
    /// </summary>
    public class ApiAgentRuntime : IAgentRuntime
    {
        #region Public-Members

        /// <inheritdoc />
        public string Name => "ApiEndpoint:" + _Endpoint.Name;

        /// <inheritdoc />
        public bool SupportsResume => false;

        /// <inheritdoc />
        public bool SupportsPlanningSessions => false;

        /// <inheritdoc />
        public event Action<int, string>? OnOutputReceived;

        /// <inheritdoc />
        public event Action<int, string>? OnStdoutReceived;

        /// <inheritdoc />
        public event Action<int, RuntimeTokenUsage>? OnTokenUsageReceived;

        /// <inheritdoc />
        public event Action<int, RuntimeTokenUsage>? OnProviderProgressReceived;

        /// <inheritdoc />
        public event Action<int>? OnProcessStarted;

        /// <inheritdoc />
        public event Action<int, int?>? OnProcessExited;

        /// <summary>
        /// Armada MCP tool access for the caller of the next run, or null for none. Only a caller-bound session
        /// credential grants access: the runtime never reads an MCP credential from the launch environment or the
        /// isolation plan, so the admiral launch credential cannot widen what a chat caller may do. A run reads
        /// this value once when it starts.
        /// </summary>
        public CallerMcpToolAccess? McpToolAccess { get; set; } = null;

        /// <summary>
        /// Whether the next run may run shell commands in its workspace through <see cref="Tools.RunCommandTool"/>.
        /// Off by default. The mission launch path turns it on, because a Worker, Test Engineer, Judge or Linter
        /// cannot do its work without git and the vessel's test command. The dashboard chat path leaves it off: a
        /// chat caller is a different principal from a dispatched mission, and a command tool there would hand the
        /// caller a shell in the admiral's container. A run reads this value once when it starts.
        /// </summary>
        public bool CommandToolEnabled { get; set; } = false;

        #endregion

        #region Private-Members

        /// <summary>Hard ceiling for one conversation; a run that is still above it after compaction stops.</summary>
        internal const int MaximumConversationBytes = 8 * 1024 * 1024;

        /// <summary>Size at which older tool results are compacted, well below the hard ceiling.</summary>
        internal const int CompactionThresholdBytes = 1024 * 1024;

        /// <summary>Most recent messages always kept whole, so the model never loses the turn it is mid-way through.</summary>
        internal const int RecentMessagesKeptWhole = 12;

        /// <summary>Prefix of a replaced tool result, so a second pass never compacts the same message twice.</summary>
        internal const string CompactedToolResultMarker = "[compacted]";

        /// <summary>Characters of an unspared tool result kept after compaction, plus a one-line note.
        /// The pairing stays; only the body shrinks.</summary>
        internal const int TruncatedHeadChars = 300;

        private static readonly ConcurrentDictionary<int, CancellationTokenSource> _Running = new ConcurrentDictionary<int, CancellationTokenSource>();

        private readonly ModelEndpoint _Endpoint;
        private readonly LoggingModule _Logging;
        private readonly int _MaxIterations;
        private readonly Func<ModelEndpoint, LoggingModule, CompletionClientBase> _ClientFactory;
        private HttpClient? _TransportClient;
        private readonly string _Header = "[ApiAgentRuntime] ";
        private StreamWriter? _LogWriter;
        private readonly object _LogLock = new object();

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate for a specific inference endpoint.
        /// </summary>
        /// <param name="endpoint">The inference model endpoint to drive.</param>
        /// <param name="logging">Logging module.</param>
        /// <param name="maxIterations">Maximum tool-call iterations before the loop stops. Clamped to 1..1000.</param>
        /// <param name="clientFactory">Optional inference-client factory seam for testing; defaults to the
        /// production <see cref="ModelEndpointClientFactory"/>.</param>
        public ApiAgentRuntime(
            ModelEndpoint endpoint,
            LoggingModule logging,
            int maxIterations = 100,
            Func<ModelEndpoint, LoggingModule, CompletionClientBase>? clientFactory = null)
        {
            _Endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
            if (!_Endpoint.Enabled) throw new InvalidOperationException("The model endpoint is disabled.");
            if (_Endpoint.Kind != ModelEndpointKindEnum.Inference) throw new InvalidOperationException("The API agent runtime requires an inference endpoint.");
            _MaxIterations = Math.Clamp(maxIterations, 1, 1000);
            _ClientFactory = clientFactory ?? new Func<ModelEndpoint, LoggingModule, CompletionClientBase>(
                (ep, log) => CreateProductionClient(ep, log));
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Whether an API-endpoint captain loop is currently running under the given synthetic process id.
        /// </summary>
        /// <param name="processId">Synthetic process id.</param>
        /// <returns>True when the loop is tracked and alive.</returns>
        public static bool IsTracked(int processId)
        {
            return _Running.ContainsKey(processId);
        }

        /// <summary>
        /// Requests cancellation for a tracked API-endpoint run without resolving its endpoint again.
        /// This permits a captain to stop after an operator disables or edits its endpoint.
        /// </summary>
        /// <param name="processId">Synthetic process id.</param>
        /// <returns>True when a tracked run received the cancellation request.</returns>
        public static bool CancelTracked(int processId)
        {
            if (!_Running.TryGetValue(processId, out CancellationTokenSource? cts)) return false;
            try
            {
                cts.Cancel();
                return true;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
        }

        /// <inheritdoc />
        public Task<int> StartAsync(
            string workingDirectory,
            string prompt,
            Dictionary<string, string>? environment = null,
            string? logFilePath = null,
            string? finalMessageFilePath = null,
            string? model = null,
            Captain? captain = null,
            bool showThinking = false,
            CancellationToken token = default,
            CaptainLaunchIsolationPlan? isolationPlan = null)
        {
            if (String.IsNullOrWhiteSpace(workingDirectory)) throw new ArgumentNullException(nameof(workingDirectory));
            token.ThrowIfCancellationRequested();

            int processId = ProcessSupervisor.AllocateSyntheticProcessId();
            CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            _Running[processId] = cts;

            try
            {
                ProcessSupervisor.RegisterSyntheticProcess(processId);
                OpenLog(logFilePath, prompt);
                OnProcessStarted?.Invoke(processId);
            }
            catch
            {
                _Running.TryRemove(processId, out CancellationTokenSource? removed);
                ProcessSupervisor.UnregisterSyntheticProcess(processId);
                try { removed?.Dispose(); } catch { }
                CloseLog();
                throw;
            }

            // Run the loop in the background so StartAsync returns the pid promptly, mirroring a process launch.
            CallerMcpToolAccess? mcpAccess = McpToolAccess;
            bool commandToolEnabled = CommandToolEnabled;
            _ = Task.Run(() => RunLoopAsync(processId, workingDirectory, prompt, model, finalMessageFilePath, mcpAccess, commandToolEnabled, cts));

            return Task.FromResult(processId);
        }

        /// <summary>
        /// Select the MCP tools a run is offered beside its workspace tools. A built-in workspace tool keeps its
        /// name, so a remote tool that repeats a taken name is dropped; the first tool of a repeated remote name
        /// wins. A caller that reports the tools a run would receive selects them through this same rule, so the
        /// report and the run resolve one selection, not two copies.
        /// </summary>
        /// <param name="builtInToolNames">Names of the built-in workspace tools the run already offers.</param>
        /// <param name="remoteTools">Tools the MCP endpoint offers this caller.</param>
        /// <returns>The remote tools to add, in the order the endpoint listed them.</returns>
        public static List<McpRemoteTool> SelectOfferedMcpTools(IEnumerable<string> builtInToolNames, IEnumerable<McpRemoteTool> remoteTools)
        {
            if (remoteTools == null) throw new ArgumentNullException(nameof(remoteTools));

            HashSet<string> taken = new HashSet<string>(StringComparer.Ordinal);
            if (builtInToolNames != null)
            {
                foreach (string name in builtInToolNames)
                {
                    if (!String.IsNullOrWhiteSpace(name)) taken.Add(name);
                }
            }

            List<McpRemoteTool> offered = new List<McpRemoteTool>();
            foreach (McpRemoteTool remote in remoteTools)
            {
                if (remote == null || String.IsNullOrWhiteSpace(remote.Name)) continue;
                if (!taken.Add(remote.Name)) continue;
                offered.Add(remote);
            }

            return offered;
        }

        /// <inheritdoc />
        public Task<AgentStopResult> StopAsync(int processId, CancellationToken token = default)
        {
            if (!_Running.TryGetValue(processId, out CancellationTokenSource? cts))
                return Task.FromResult(AgentStopResult.NotRunning());

            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The loop finished and disposed its source between the lookup and the cancel.
                return Task.FromResult(AgentStopResult.NotRunning());
            }
            catch (AggregateException)
            {
                // A cancellation callback threw after the cancellation was requested; the loop is still stopping.
            }
            return Task.FromResult(AgentStopResult.Stopped());
        }

        /// <inheritdoc />
        public Task<bool> IsRunningAsync(int processId, CancellationToken token = default)
        {
            return Task.FromResult(_Running.ContainsKey(processId));
        }

        #endregion

        #region Private-Methods

        private async Task RunLoopAsync(
            int processId,
            string workingDirectory,
            string prompt,
            string? model,
            string? finalMessageFilePath,
            CallerMcpToolAccess? mcpAccess,
            bool commandToolEnabled,
            CancellationTokenSource cts)
        {
            int exitCode = 0;
            string finalText = String.Empty;
            CancellationToken token = cts.Token;
            McpToolClient? mcpClient = null;

            try
            {
                using CompletionClientBase client = _ClientFactory(_Endpoint, _Logging);
                string? effectiveModel = String.IsNullOrWhiteSpace(model) ? _Endpoint.Model : model;
                if (!String.IsNullOrWhiteSpace(effectiveModel)) client.Model = effectiveModel;

                TaskPlan taskPlan = new TaskPlan();
                BuiltInToolRegistry registry = new BuiltInToolRegistry(taskPlan, commandToolEnabled);
                List<PolyToolDefinition> tools = BuildToolDefinitions(registry);

                HashSet<string> mcpToolNames = new HashSet<string>(StringComparer.Ordinal);
                if (mcpAccess != null)
                {
                    mcpClient = await ConnectMcpToolsAsync(processId, mcpAccess, tools, mcpToolNames, token).ConfigureAwait(false);
                }

                List<ChatMessage> messages = new List<ChatMessage>();
                messages.Add(ChatMessage.System(BuildSystemPrompt(workingDirectory)));
                messages.Add(ChatMessage.User(prompt));

                // Cache capability is announced once, never inferred from a zero counter.
                string cacheCapability = _Endpoint.Provider == ModelProviderEnum.Anthropic
                    ? "Anthropic prompt caching is requested through the stable system and launch prefix; provider minimums still apply."
                    : "this transport cannot mark a prompt-cache breakpoint; provider automatic caching may still apply.";
                _Logging.Info(_Header + "process " + processId + ": " + cacheCapability);

                for (int iteration = 0; iteration < _MaxIterations; iteration++)
                {
                    token.ThrowIfCancellationRequested();
                    await EnsureConversationBoundsAsync(processId, messages, prompt, token).ConfigureAwait(false);

                    ToolChatRequest request = new ToolChatRequest();
                    request.Messages = messages;
                    request.Tools = tools;
                    if (!String.IsNullOrWhiteSpace(effectiveModel)) request.Model = effectiveModel;

                    ToolChatStreamingResponse streamedResponse = await client.ToolChatStreamingAsync(request, token).ConfigureAwait(false);
                    if (streamedResponse.Chunks != null)
                    {
                        await foreach (ToolChatStreamingChunk _ in streamedResponse.Chunks.WithCancellation(token).ConfigureAwait(false))
                        {
                        }
                    }

                    ToolChatStreamingResponse response = streamedResponse;
                    EnsureResponseBounds(response);
                    RuntimeTokenUsage progress = new RuntimeTokenUsage
                    {
                        Runtime = Name,
                        Model = effectiveModel ?? String.Empty,
                        Source = "api_endpoint"
                    };
                    if (response.Usage != null)
                    {
                        RuntimeTokenUsage usage = new RuntimeTokenUsage
                        {
                            Runtime = Name,
                            Model = effectiveModel ?? String.Empty,
                            Source = "api_endpoint",
                            InputTokens = response.Usage.PromptTokens ?? 0,
                            OutputTokens = response.Usage.CompletionTokens ?? 0,
                            CacheReadTokens = response.Usage.CachedPromptTokens ?? 0,
                            CacheWriteTokens = response.Usage.CacheCreationTokens ?? 0,
                            ReasoningTokens = response.Usage.ReasoningTokens ?? 0,
                            ProviderTotalTokens = response.Usage.TotalTokens
                        };
                        PublishTokenUsage(processId, usage);
                    }
                    try { OnProviderProgressReceived?.Invoke(processId, progress); } catch { }

                    if (!response.Success)
                    {
                        string failure = String.IsNullOrWhiteSpace(response.Error)
                            ? "inference request returned an unsuccessful response."
                            : response.Error;
                        Emit(processId, "[error] inference call failed: " + failure);
                        exitCode = 1;
                        break;
                    }

                    if (response.ToolCalls == null)
                        response.ToolCalls = new List<ToolCall>();

                    // Reasoning must never reach the text a terminal marker or a Judge verdict is parsed
                    // from. PolyPrompt keeps the provider's reasoning channel out of Text already, but a
                    // proxy can still leak think markup INTO the content, and a stray closing tag has been
                    // observed in a mission log. Strip it before the text is emitted or kept as the final
                    // message; response.Reasoning is deliberately never emitted and never parsed.
                    string visibleText = StripReasoningMarkup(response.Text);
                    if (!String.IsNullOrEmpty(visibleText))
                    {
                        finalText = ToolExecution.LimitOutput(visibleText, ToolSafetyLimits.MaxProcessOutputBytes);
                        Emit(processId, finalText);
                    }

                    if (response.ToolCalls.Count > 16)
                    {
                        Emit(processId, "[error] inference response requested too many tools in one turn.");
                        exitCode = 1;
                        break;
                    }
                    messages.Add(response.ToAssistantMessage());

                    if (response.ToolCalls == null || response.ToolCalls.Count == 0)
                    {
                        // No more tool calls: the model has finished.
                        break;
                    }

                    foreach (ToolCall call in response.ToolCalls)
                    {
                        token.ThrowIfCancellationRequested();
                        string resultContent = mcpClient != null && mcpToolNames.Contains(call.Name)
                            ? await ExecuteMcpToolAsync(processId, mcpClient, call, workingDirectory, token).ConfigureAwait(false)
                            : await ExecuteToolAsync(processId, registry, call, workingDirectory, token).ConfigureAwait(false);
                        messages.Add(ChatMessage.ToolResult(call.Id, call.Name, resultContent));
                    }

                    if (iteration == _MaxIterations - 1)
                    {
                        Emit(processId, "[error] maximum tool iterations reached (" + _MaxIterations + "); the run did not complete.");
                        exitCode = 2;
                    }
                }

                if (exitCode == 0) WriteFinalMessage(finalMessageFilePath, finalText);
            }
            catch (OperationCanceledException)
            {
                exitCode = -1;
                Emit(processId, "[cancelled] the captain run was stopped.");
            }
            catch (Exception e)
            {
                exitCode = 1;
                _Logging.Warn(_Header + "loop error for process " + processId + ": " + e.Message);
                Emit(processId, "[error] " + e.Message);
            }
            finally
            {
                mcpClient?.Dispose();
                CloseLog();
                try { _TransportClient?.Dispose(); } catch { }
                _TransportClient = null;
                _Running.TryRemove(processId, out CancellationTokenSource? _);
                ProcessSupervisor.UnregisterSyntheticProcess(processId);
                try { cts.Dispose(); } catch { }
                RaiseProcessExited(processId, exitCode);
            }
        }

        private async Task<string> ExecuteToolAsync(int processId, BuiltInToolRegistry registry, ToolCall call, string workingDirectory, CancellationToken token)
        {
            string argsJson = String.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson;
            string? detail = ReadPrimaryArgument(argsJson);

            try
            {
                ToolResult result = await registry.ExecuteAsync(call.Id ?? String.Empty, call.Name, argsJson, workingDirectory, token).ConfigureAwait(false);
                EmitToolActivity(
                    processId,
                    call.Name,
                    detail,
                    result.Success ? StructuredRuntimeLogFormatter.OkStatus : StructuredRuntimeLogFormatter.ErrorStatus,
                    workingDirectory,
                    result.Success ? null : ReadFailureClass(result.Content));
                return result.Content ?? String.Empty;
            }
            catch (Exception ex)
            {
                string failureClass = ClassifyToolException(ex);
                string message = "Tool execution failed: " + Truncate(ex.Message, 200);
                EmitToolActivity(processId, call.Name, detail, StructuredRuntimeLogFormatter.ErrorStatus, workingDirectory, failureClass);
                return JsonSerializer.Serialize(new { error = failureClass, message });
            }
        }

        /// <summary>
        /// Remove model reasoning markup from assistant text. A provider that serves a reasoning model
        /// through an OpenAI-compatible shim normally streams reasoning on its own channel, but a leaked
        /// think block or a lone closing tag inside the content reaches the same text a terminal marker
        /// and a Judge verdict are parsed from, and a review without exactly one standalone verdict line is
        /// discarded and re-run.
        /// </summary>
        /// <remarks>
        /// A matched block is dropped whole. A lone closing tag means the opener was lost, so everything
        /// before it was reasoning and is dropped with it. A lone opening tag means the model was still
        /// reasoning at the end, so everything after it is dropped. Text with no markup is returned
        /// unchanged, including its whitespace, so an ordinary answer is never reshaped.
        /// </remarks>
        /// <param name="text">Assistant text as the client returned it.</param>
        /// <returns>Text safe to emit and to parse for markers.</returns>
        internal static string StripReasoningMarkup(string? text)
        {
            if (String.IsNullOrEmpty(text)) return String.Empty;
            if (text!.IndexOf("<think", StringComparison.OrdinalIgnoreCase) < 0
                && text.IndexOf("</think", StringComparison.OrdinalIgnoreCase) < 0)
            {
                return text;
            }

            string stripped = System.Text.RegularExpressions.Regex.Replace(
                text,
                "<think[^>]*>.*?</think\\s*>",
                String.Empty,
                System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            int lastClose = stripped.LastIndexOf("</think", StringComparison.OrdinalIgnoreCase);
            if (lastClose >= 0)
            {
                int closeEnd = stripped.IndexOf('>', lastClose);
                stripped = closeEnd >= 0 ? stripped.Substring(closeEnd + 1) : String.Empty;
            }

            int firstOpen = stripped.IndexOf("<think", StringComparison.OrdinalIgnoreCase);
            if (firstOpen >= 0) stripped = stripped.Substring(0, firstOpen);

            return stripped.Trim();
        }

        /// <summary>
        /// Name the class of a tool failure that came back as an exception. Every one of these used to be
        /// reported as invalid arguments, which made a path refused at the workspace boundary and a file
        /// above the read limit indistinguishable from a malformed tool-call payload, in the log and in the
        /// result the model reads.
        /// </summary>
        /// <param name="exception">Exception thrown by the tool.</param>
        /// <returns>Short failure class.</returns>
        internal static string ClassifyToolException(Exception exception)
        {
            if (exception is WorkspaceBoundaryException) return "boundary_refused";
            if (exception is WorkspaceEnumerationLimitException) return "enumeration_limit";
            if (exception is ToolSizeLimitException) return "size_limit";
            if (exception is JsonException) return "invalid_arguments";

            // ToolArgumentParser wraps a malformed payload's JsonException in an ArgumentException, and a
            // missing required argument is an ArgumentException too; both are the caller's arguments.
            if (exception is ArgumentException) return "invalid_arguments";
            if (exception is OperationCanceledException) return "cancelled";
            if (exception is UnauthorizedAccessException) return "permission_denied";
            if (exception is FileNotFoundException || exception is DirectoryNotFoundException) return "not_found";
            if (exception is IOException) return "io_error";
            return "tool_failed";
        }

        /// <summary>
        /// Read the failure class a tool already reported in its own result. Tool results carry
        /// <c>{ "error": "...", "message": "..." }</c>; only the class is taken, because the message
        /// carries absolute workspace paths that must not reach a log line.
        /// </summary>
        /// <param name="content">Tool result content.</param>
        /// <returns>Failure class, or null when the content does not name one.</returns>
        internal static string? ReadFailureClass(string? content)
        {
            if (String.IsNullOrWhiteSpace(content)) return null;

            try
            {
                using JsonDocument document = JsonDocument.Parse(content);
                if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
                if (!document.RootElement.TryGetProperty("error", out JsonElement error)) return null;
                if (error.ValueKind != JsonValueKind.String) return null;
                return error.GetString();
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>
        /// Connect to the Armada MCP endpoint with the caller's credential and add the tools it offers that caller.
        /// A built-in workspace tool keeps its name: a remote tool with the same name is not added. When the
        /// endpoint cannot be reached or refuses the credential, the run continues with the workspace tools
        /// only and the reason is logged, never written into the reply.
        /// </summary>
        private async Task<McpToolClient?> ConnectMcpToolsAsync(
            int processId,
            CallerMcpToolAccess access,
            List<PolyToolDefinition> tools,
            HashSet<string> mcpToolNames,
            CancellationToken token)
        {
            HashSet<string> builtInNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (PolyToolDefinition builtIn in tools) builtInNames.Add(builtIn.Name);

            McpToolClient mcpClient = new McpToolClient(access.Endpoint, access.SessionToken, _Logging);
            try
            {
                await mcpClient.InitializeAsync(token).ConfigureAwait(false);
                List<McpRemoteTool> remoteTools = await mcpClient.ListToolsAsync(token).ConfigureAwait(false);
                foreach (McpRemoteTool remote in SelectOfferedMcpTools(builtInNames, remoteTools))
                {
                    tools.Add(PolyToolDefinition.Function(remote.Name, remote.Description, remote.InputSchema));
                    mcpToolNames.Add(remote.Name);
                }

                WriteLog("[mcp] " + mcpToolNames.Count + " Armada MCP tool(s) available to process " + processId);
                return mcpClient;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                mcpClient.Dispose();
                throw;
            }
            catch (Exception ex) when (ex is McpClientException || ex is HttpRequestException || ex is TaskCanceledException)
            {
                mcpClient.Dispose();
                mcpToolNames.Clear();
                string reason = "Armada MCP tools are unavailable for process " + processId + ": " + ex.Message;
                _Logging.Warn(_Header + reason);
                WriteLog("[mcp] " + reason);
                return null;
            }
        }

        private async Task<string> ExecuteMcpToolAsync(int processId, McpToolClient mcpClient, ToolCall call, string workingDirectory, CancellationToken token)
        {
            string argsJson = String.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson;
            try
            {
                McpToolCallResult result = await mcpClient.CallToolAsync(call.Name, argsJson, token).ConfigureAwait(false);
                EmitToolActivity(
                    processId,
                    call.Name,
                    null,
                    result.IsError ? StructuredRuntimeLogFormatter.ErrorStatus : StructuredRuntimeLogFormatter.OkStatus,
                    workingDirectory,
                    result.IsError ? (ReadFailureClass(result.Text) ?? "mcp_tool_error") : null);
                return ToolExecution.LimitOutput(result.Text, ToolSafetyLimits.MaxProcessOutputBytes);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is McpClientException || ex is HttpRequestException || ex is TaskCanceledException)
            {
                EmitToolActivity(processId, call.Name, null, StructuredRuntimeLogFormatter.ErrorStatus, workingDirectory, "mcp_tool_failed");
                return JsonSerializer.Serialize(new { error = "mcp_tool_failed", message = Truncate(ex.Message, 500) });
            }
        }

        // Tool calls are runtime activity, not the model's answer: they use the shared activity record so
        // chat, planning and mission output separate them from answer text. Only the primary argument is
        // rendered; file content and other arguments are never copied into activity.
        private void EmitToolActivity(int processId, string? toolName, string? detail, string status, string workingDirectory, string? reason = null)
        {
            string record = StructuredRuntimeLogFormatter.BuildToolActivity(
                String.IsNullOrWhiteSpace(toolName) ? "unknown" : toolName,
                detail,
                status,
                workingDirectory,
                reason);
            Emit(processId, record);
        }

        private static string? ReadPrimaryArgument(string argsJson)
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(argsJson);
                if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
                // "command" first: for run_command the command text IS the record, exactly as a CLI
                // harness's activity line shows the shell command it ran.
                foreach (string name in new[] { "command", "file_path", "path", "pattern" })
                {
                    if (document.RootElement.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String)
                        return value.GetString();
                }
                return null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static void EnsureResponseBounds(ToolChatStreamingResponse response)
        {
            const int maximumResponseBytes = 4 * 1024 * 1024;
            int responseBytes = Encoding.UTF8.GetByteCount(response.Text ?? String.Empty);
            if (response.ToolCalls != null)
            {
                foreach (ToolCall call in response.ToolCalls)
                    responseBytes += Encoding.UTF8.GetByteCount(call.ArgumentsJson ?? String.Empty);
            }

            if (responseBytes > maximumResponseBytes)
                throw new InvalidDataException("The model response exceeded the allowed response limit.");
        }

        /// <summary>
        /// Keep the conversation inside its ceiling by compacting it, and only throw when compaction cannot
        /// bring it back. Throwing alone converted a long run into a LOST run: the work was done and no
        /// result came back. Every message is kept so the assistant tool-call and tool-result pairing the
        /// provider requires is never broken; what shrinks is the CONTENT of older tool results, which is
        /// where the bytes are. The system prompt, the launch prompt and the most recent exchanges stay
        /// whole.
        /// </summary>
        /// <param name="messages">Conversation so far; mutated in place when it is compacted.</param>
        /// <returns>Number of tool results whose content was replaced, or zero when nothing was compacted.</returns>
        internal static int CompactConversation(List<ChatMessage> messages)
        {
            return CompactConversation(messages, Array.Empty<int>());
        }

        /// <summary>
        /// Whether the message at an index is a tool result the deterministic pass would replace.
        /// </summary>
        /// <param name="messages">Conversation so far.</param>
        /// <param name="index">Message index.</param>
        /// <returns>True when the message is an eligible, not-yet-compacted tool result.</returns>
        private static bool IsCompactionCandidate(List<ChatMessage> messages, int index)
        {
            // The first two messages are the system prompt and the launch prompt: the mission's own
            // instructions, which a captain needs at the last turn as much as the first.
            if (index < 2) return false;
            ChatMessage message = messages[index];
            if (!String.Equals(message.Role, "tool", StringComparison.OrdinalIgnoreCase)) return false;
            if (String.IsNullOrEmpty(message.Content)) return false;
            if (message.Content!.StartsWith(CompactedToolResultMarker, StringComparison.Ordinal)) return false;
            return Encoding.UTF8.GetByteCount(message.Content!) > CompactedToolResultMarker.Length;
        }

        /// <summary>
        /// Compact the conversation. A null <paramref name="sparedIndices"/> is the ceiling override:
        /// every eligible result is truncated, including diagnostic lines. An empty collection is the
        /// ordinary pass: diagnostic and test-total lines stay because that keep is a shape rule.
        /// </summary>
        /// <param name="messages">Conversation so far; mutated in place when it is compacted.</param>
        /// <param name="sparedIndices">Unused except null meaning force, including diagnostic keep skip.
        /// Ordinary callers pass an empty collection.</param>
        /// <returns>Number of tool results whose content was replaced, or zero when nothing was compacted.</returns>
        internal static int CompactConversation(List<ChatMessage> messages, IReadOnlyCollection<int>? sparedIndices)
        {
            if (messages == null || messages.Count <= RecentMessagesKeptWhole) return 0;
            if (MeasureConversationBytes(messages) <= CompactionThresholdBytes) return 0;

            int compacted = 0;
            int lastProtected = messages.Count - RecentMessagesKeptWhole;
            bool force = sparedIndices == null;
            for (int index = 0; index < lastProtected; index++)
            {
                if (!force && sparedIndices!.Contains(index)) continue;
                if (!IsCompactionCandidate(messages, index)) continue;

                ChatMessage message = messages[index];
                string content = message.Content ?? String.Empty;
                if (!force && ToolOutputRetention.IsProtected(content)) continue;

                int droppedBytes = Encoding.UTF8.GetByteCount(content);
                string head = content.Length <= TruncatedHeadChars ? content : content.Substring(0, TruncatedHeadChars);
                message.Content = CompactedToolResultMarker + " " + (message.ToolName ?? "tool")
                    + " returned " + droppedBytes.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " bytes earlier in this run. Head kept:\n" + head;
                compacted++;
            }

            return compacted;
        }

        private static int MeasureConversationBytes(List<ChatMessage> messages)
        {
            return Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(messages));
        }

        internal Task EnsureConversationBoundsAsync(int processId, List<ChatMessage> messages, string goal, CancellationToken token)
        {
            if (MeasureConversationBytes(messages) <= CompactionThresholdBytes) return Task.CompletedTask;

            int compacted = CompactConversation(messages, Array.Empty<int>());
            if (compacted > 0)
            {
                // Reported through the log, never through Emit: the emitted stream is the stream a terminal
                // marker and a Judge verdict are parsed from, and it holds only canonical activity records.
                _Logging.Warn(_Header + "process " + processId + ": compacted " + compacted
                    + " earlier tool result(s) to stay inside the context limit; the mission instructions and the recent turns are unchanged.");
            }

            int conversationBytes = MeasureConversationBytes(messages);
            if (conversationBytes > MaximumConversationBytes)
            {
                int forced = CompactConversation(messages, null);
                if (forced > 0)
                {
                    _Logging.Warn(_Header + "process " + processId + ": the conversation was still over the hard ceiling, so "
                        + forced + " remaining result(s) were truncated including diagnostic lines.");
                    conversationBytes = MeasureConversationBytes(messages);
                }
            }

            if (conversationBytes > MaximumConversationBytes)
                throw new InvalidDataException("The model conversation exceeded the allowed context limit.");
            return Task.CompletedTask;
        }

        private CompletionClientBase CreateProductionClient(ModelEndpoint endpoint, LoggingModule logging)
        {
            // PolyPrompt clients apply authentication for OpenAI, Anthropic, and Gemini themselves.
            // Ollama does not, so retain the endpoint credential on that transport only.
            bool transportCredentials = endpoint.Provider == ModelProviderEnum.Ollama;
            HttpClient transport = ModelEndpointClientFactory.CreateHttpClient(endpoint, transportCredentials);
            _TransportClient = transport;
            try
            {
                return endpoint.Provider switch
                {
                    ModelProviderEnum.Ollama => new OllamaClient(endpoint.BaseUrl, endpoint.ApiKey, logging, transport),
                    ModelProviderEnum.Anthropic => new CachedAnthropicClient(endpoint.BaseUrl, endpoint.ApiKey, logging, transport),
                    ModelProviderEnum.Gemini => new GeminiClient(endpoint.BaseUrl, endpoint.ApiKey, logging, transport),
                    ModelProviderEnum.VoyageAI => throw new InvalidOperationException("VoyageAI does not support inference endpoints."),
                    ModelProviderEnum.OpenAI or ModelProviderEnum.OpenAICompatible => new OpenAiClient(endpoint.BaseUrl, endpoint.ApiKey, logging, transport),
                    _ => throw new InvalidOperationException("Unsupported model endpoint provider.")
                };
            }
            catch
            {
                transport.Dispose();
                _TransportClient = null;
                throw;
            }
        }

        private List<PolyToolDefinition> BuildToolDefinitions(BuiltInToolRegistry registry)
        {
            List<PolyToolDefinition> definitions = new List<PolyToolDefinition>();
            foreach (ArmadaToolDefinition tool in registry.GetToolDefinitions())
            {
                Dictionary<string, object> parameters = SchemaToDictionary(tool.ParametersSchema);
                definitions.Add(PolyToolDefinition.Function(tool.Name, tool.Description, parameters));
            }

            return definitions;
        }

        private static Dictionary<string, object> SchemaToDictionary(object schema)
        {
            try
            {
                string json = JsonSerializer.Serialize(schema);
                Dictionary<string, object>? parsed = JsonSerializer.Deserialize<Dictionary<string, object>>(json);
                return parsed ?? new Dictionary<string, object>();
            }
            catch
            {
                return new Dictionary<string, object>();
            }
        }

        private string BuildSystemPrompt(string workingDirectory)
        {
            return
                "You are an autonomous coding agent operating as an Armada captain. You are working inside a git " +
                "worktree at " + workingDirectory + ". Use the tools this session provides -- the workspace tools, and " +
                "the Armada MCP tools when they are listed beside them -- to inspect and modify files and to read or " +
                "change Armada state, in order to complete the mission described by the user. Never claim a tool that " +
                "is not provided. Make focused changes, verify your " +
                "work, and when the mission is complete stop calling tools and reply with a concise summary of what " +
                "you changed. If the mission instructions define [ARMADA:...] signals, emit them as plain text lines.";
        }

        private void Emit(int processId, string line)
        {
            if (String.IsNullOrEmpty(line)) return;
            WriteLog(line);
            try { OnOutputReceived?.Invoke(processId, line); } catch { }
            try { OnStdoutReceived?.Invoke(processId, line); } catch { }
        }

        private void PublishTokenUsage(int processId, RuntimeTokenUsage usage)
        {
            try { OnTokenUsageReceived?.Invoke(processId, usage); } catch { }
        }

        private void RaiseProcessExited(int processId, int exitCode)
        {
            Delegate[] handlers = OnProcessExited?.GetInvocationList() ?? Array.Empty<Delegate>();
            foreach (Delegate handler in handlers)
            {
                try { ((Action<int, int?>)handler).Invoke(processId, exitCode); }
                catch { }
            }
        }

        private static string Truncate(string? value, int max)
        {
            if (String.IsNullOrEmpty(value)) return String.Empty;
            string single = value!.Replace("\r", " ").Replace("\n", " ");
            return single.Length <= max ? single : single.Substring(0, max) + "...";
        }

        private void OpenLog(string? logFilePath, string prompt)
        {
            if (String.IsNullOrEmpty(logFilePath)) return;
            try
            {
                lock (_LogLock)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(logFilePath)!);
                    _LogWriter = new StreamWriter(logFilePath, append: true) { AutoFlush = true };
                    string timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
                    _LogWriter.WriteLine("[" + timestamp + "] API-endpoint captain starting: " + _Endpoint.Name + " (" + _Endpoint.Provider + "/" + (_Endpoint.Model ?? "default") + ")");
                    if (!String.IsNullOrEmpty(prompt)) _LogWriter.WriteLine(prompt);
                    _LogWriter.WriteLine(String.Empty);
                }
            }
            catch
            {
                _LogWriter = null;
            }
        }

        private void WriteLog(string data)
        {
            lock (_LogLock)
            {
                try { _LogWriter?.WriteLine(data); }
                catch (ObjectDisposedException) { }
                catch { }
            }
        }

        private void CloseLog()
        {
            lock (_LogLock)
            {
                try { _LogWriter?.Flush(); } catch { }
                try { _LogWriter?.Dispose(); } catch { }
                _LogWriter = null;
            }
        }

        private void WriteFinalMessage(string? finalMessageFilePath, string finalText)
        {
            if (String.IsNullOrEmpty(finalMessageFilePath) || String.IsNullOrEmpty(finalText)) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(finalMessageFilePath)!);
                File.WriteAllText(finalMessageFilePath, finalText);
            }
            catch (Exception e)
            {
                _Logging.Debug(_Header + "could not write final message file: " + e.Message);
            }
        }

        #endregion
    }
}
