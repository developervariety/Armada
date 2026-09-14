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

        #endregion

        #region Private-Members

        // Synthetic process ids start well above any real OS process id so a stray Process.GetProcessById does
        // not collide with an unrelated live process.
        private static int _PidCounter = 2_000_000_000;
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

            int processId = Interlocked.Increment(ref _PidCounter);
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
            _ = Task.Run(() => RunLoopAsync(processId, workingDirectory, prompt, model, finalMessageFilePath, cts));

            return Task.FromResult(processId);
        }

        /// <inheritdoc />
        public Task StopAsync(int processId, CancellationToken token = default)
        {
            if (_Running.TryGetValue(processId, out CancellationTokenSource? cts))
            {
                try { cts.Cancel(); } catch { }
            }

            return Task.CompletedTask;
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
            CancellationTokenSource cts)
        {
            int exitCode = 0;
            string finalText = String.Empty;
            CancellationToken token = cts.Token;

            try
            {
                using CompletionClientBase client = _ClientFactory(_Endpoint, _Logging);
                string? effectiveModel = String.IsNullOrWhiteSpace(model) ? _Endpoint.Model : model;
                if (!String.IsNullOrWhiteSpace(effectiveModel)) client.Model = effectiveModel;

                TaskPlan taskPlan = new TaskPlan();
                BuiltInToolRegistry registry = new BuiltInToolRegistry(taskPlan);
                List<PolyToolDefinition> tools = BuildToolDefinitions(registry);

                List<ChatMessage> messages = new List<ChatMessage>();
                messages.Add(ChatMessage.System(BuildSystemPrompt(workingDirectory)));
                messages.Add(ChatMessage.User(prompt));

                for (int iteration = 0; iteration < _MaxIterations; iteration++)
                {
                    token.ThrowIfCancellationRequested();
                    EnsureConversationBounds(messages);

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

                    if (!String.IsNullOrEmpty(response.Text))
                    {
                        finalText = ToolExecution.LimitOutput(response.Text!, ToolSafetyLimits.MaxProcessOutputBytes);
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
                        string resultContent = await ExecuteToolAsync(processId, registry, call, workingDirectory, token).ConfigureAwait(false);
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
                EmitToolActivity(processId, call.Name, detail, result.Success ? StructuredRuntimeLogFormatter.OkStatus : StructuredRuntimeLogFormatter.ErrorStatus, workingDirectory);
                return result.Content ?? String.Empty;
            }
            catch (Exception ex)
            {
                string message = "Tool execution failed: " + Truncate(ex.Message, 200);
                EmitToolActivity(processId, call.Name, detail, StructuredRuntimeLogFormatter.ErrorStatus, workingDirectory);
                return JsonSerializer.Serialize(new { error = "invalid_arguments", message });
            }
        }

        // Tool calls are runtime activity, not the model's answer: they use the shared activity record so
        // chat, planning and mission output separate them from answer text. Only the primary argument is
        // rendered; file content and other arguments are never copied into activity.
        private void EmitToolActivity(int processId, string? toolName, string? detail, string status, string workingDirectory)
        {
            string record = StructuredRuntimeLogFormatter.BuildToolActivity(
                String.IsNullOrWhiteSpace(toolName) ? "unknown" : toolName,
                detail,
                status,
                workingDirectory);
            Emit(processId, record);
        }

        private static string? ReadPrimaryArgument(string argsJson)
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(argsJson);
                if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
                foreach (string name in new[] { "file_path", "path", "pattern" })
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

        private static void EnsureConversationBounds(List<ChatMessage> messages)
        {
            const int maximumConversationBytes = 8 * 1024 * 1024;
            int conversationBytes = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(messages));
            if (conversationBytes > maximumConversationBytes)
                throw new InvalidDataException("The model conversation exceeded the allowed context limit.");
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
                    ModelProviderEnum.Anthropic => new AnthropicClient(endpoint.BaseUrl, endpoint.ApiKey, logging, transport),
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
                "worktree at " + workingDirectory + ". Use only the provided workspace tools to inspect and modify files " +
                "in order to complete the mission described by the user. Make focused changes, verify your " +
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
