namespace Armada.Runtimes
{
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using SyslogLogging;

    /// <summary>
    /// Agent runtime adapter for OpenAI Codex CLI.
    /// </summary>
    /// <remarks>
    /// Per-invocation reasoning effort: when <c>CaptainRuntimeOptions.ReasoningEffort</c> is set
    /// for a captain, Codex CLI receives <c>-c model_reasoning_effort=&lt;value&gt;</c> on each call.
    /// Accepted values: low|medium|high. The flag is injected before
    /// <c>--output-last-message</c> and the prompt argument so Codex parses it as a config
    /// override rather than prompt text.
    ///
    /// ReasoningEffort is silently ignored if absent from the captain's RuntimeOptionsJson,
    /// preserving backward compatibility for captains provisioned before the setting existed.
    /// </remarks>
    public class CodexRuntime : BaseAgentRuntime
    {
        #region Public-Members

        /// <summary>
        /// Runtime display name.
        /// </summary>
        public override string Name => "Codex";

        /// <summary>
        /// Codex does not support session resume.
        /// </summary>
        public override bool SupportsResume => false;

        /// <summary>
        /// Path to the codex CLI executable.
        /// </summary>
        public string ExecutablePath
        {
            get => _ExecutablePath;
            set
            {
                if (String.IsNullOrEmpty(value)) throw new ArgumentNullException(nameof(ExecutablePath));
                _ExecutablePath = value;
            }
        }

        /// <summary>
        /// Approval mode for codex operations.
        /// </summary>
        public string ApprovalMode { get; set; } = "full-auto";

        #endregion

        #region Private-Members

        private string _ExecutablePath = "codex";

        private readonly Armada.Core.Settings.ModelProvidersSettings _ModelProviders;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="logging">Logging module.</param>
        public CodexRuntime(LoggingModule logging) : this(logging, null)
        {
        }

        /// <summary>
        /// Instantiate with a provider registry.
        /// </summary>
        /// <param name="logging">Logging module.</param>
        /// <param name="modelProviders">
        /// External model provider registry; null uses the built-in default set.
        /// </param>
        public CodexRuntime(LoggingModule logging, Armada.Core.Settings.ModelProvidersSettings? modelProviders)
            : base(logging)
        {
            _ModelProviders = modelProviders ?? new Armada.Core.Settings.ModelProvidersSettings();
        }

        #endregion

        #region Private-Methods

        /// <summary>
        /// Point THIS captain process at an external provider's OpenAI-compatible endpoint
        /// when its model resolves to one. Codex does not honor base-URL environment
        /// variables: the endpoint is wired through a <c>--profile</c> config layer
        /// (see <see cref="EnsureProviderProfile"/>) and the credential through
        /// <c>ARMADA_PROVIDER_KEY</c>, the profile's <c>env_key</c>.
        /// </summary>
        /// <param name="startInfo">Start info for the captain process being launched.</param>
        /// <param name="captain">Captain being launched; may be null.</param>
        /// <param name="model">Model id to resolve; the captain's own model wins when both exist.</param>
        protected override void ApplyEnvironment(ProcessStartInfo startInfo, Captain? captain, string? model = null)
        {
            ResolvedModelProvider? resolved = ModelProviderResolver.Resolve(captain, captain?.Model ?? model, _ModelProviders);
            if (resolved == null) return;

            startInfo.Environment["ARMADA_PROVIDER_KEY"] = resolved.ApiKey;

            // An inherited credential would outrank or collide with the routed key, so clear it
            // for this child only; a native captain beside it keeps its own.
            startInfo.Environment.Remove("CODEX_API_KEY");
        }

        /// <summary>
        /// Write the codex profile layer that routes a resolved provider captain to its
        /// endpoint. Codex layers <c>$CODEX_HOME/&lt;name&gt;.config.toml</c> on top of the
        /// base config via <c>--profile &lt;name&gt;</c>; the profile names the provider,
        /// its base URL, and the environment variable holding the credential.
        /// </summary>
        /// <param name="resolved">Resolved provider for the captain being launched.</param>
        private void EnsureProviderProfile(ResolvedModelProvider resolved)
        {
            if (resolved == null) return;

            string profileName = ProviderProfileName(resolved);
            string codexHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string codexDir = Path.Combine(codexHome, ".codex");
            Directory.CreateDirectory(codexDir);

            string path = Path.Combine(codexDir, profileName + ".config.toml");
            string content =
                "model_provider = \"" + profileName + "\"\n" +
                "\n" +
                "[model_providers." + profileName + "]\n" +
                "name = \"" + profileName + "\"\n" +
                "base_url = \"" + resolved.BaseUrl + "\"\n" +
                "wire_api = \"responses\"\n" +
                "env_key = \"ARMADA_PROVIDER_KEY\"\n";

            if (!File.Exists(path) || !String.Equals(File.ReadAllText(path), content, StringComparison.Ordinal))
            {
                File.WriteAllText(path, content);
            }
        }

        /// <summary>
        /// Resolve the codex profile name for a captain: the registered provider id when
        /// present, otherwise a host-derived name so custom-endpoint captains on different
        /// providers never share one profile file (a shared file would let the last
        /// launcher overwrite the other provider's base URL).
        /// </summary>
        /// <param name="resolved">Resolved provider for the captain being launched.</param>
        /// <returns>Profile name usable as <c>--profile</c> and as the config file stem.</returns>
        private static string ProviderProfileName(ResolvedModelProvider resolved)
        {
            if (!String.IsNullOrWhiteSpace(resolved.ProviderId)) return resolved.ProviderId;

            string host = String.Empty;
            if (Uri.TryCreate(resolved.BaseUrl, UriKind.Absolute, out Uri? uri) && !String.IsNullOrWhiteSpace(uri.Host))
            {
                host = uri.Host;
            }
            else
            {
                host = resolved.BaseUrl;
            }

            // "https://provider-a.example.com/v1" -> "custom-provider-a-example-com"
            string sanitized = host.Replace(".", "-").Replace(":", "-").Replace("/", "-");
            return "custom-" + sanitized;
        }

        /// <summary>
        /// Codex receives its prompt as a CLI argument, not via stdin. Suppressing the stdin pipe
        /// prevents Codex from detecting a piped context and printing "Reading additional input
        /// from stdin..." to stderr on every invocation.
        /// </summary>
        protected override bool RedirectStdin => false;

        /// <summary>
        /// Codex exec streams its ENTIRE human-readable working transcript (session header,
        /// reasoning, every exec command and its full stdout) to stderr, bloating mission logs
        /// 75-220x versus Claude/Cursor. Suppressing the stderr log-FILE write keeps logs bounded;
        /// the final answer is still captured via --output-last-message and echoed on exit, and
        /// heartbeat/progress detection is preserved because OnOutputReceived still fires for stderr.
        /// </summary>
        protected override bool WriteStderrToLogFile => false;

        /// <summary>
        /// Start a codex captain process. When the captain's model resolves to an external
        /// provider, the provider profile layer is written before the CLI launches so the
        /// <c>--profile</c> argument and the routed credential are both in place.
        /// </summary>
        /// <inheritdoc />
        public override async Task<int> StartAsync(
            string workingDirectory,
            string prompt,
            Dictionary<string, string>? environment = null,
            string? logFilePath = null,
            string? finalMessageFilePath = null,
            string? model = null,
            Captain? captain = null,
            CancellationToken token = default)
        {
            ResolvedModelProvider? resolved = ModelProviderResolver.Resolve(captain, captain?.Model ?? model, _ModelProviders);
            if (resolved != null)
            {
                EnsureProviderProfile(resolved);
            }

            return await base.StartAsync(
                workingDirectory,
                prompt,
                environment,
                logFilePath,
                finalMessageFilePath,
                model,
                captain,
                token).ConfigureAwait(false);
        }

        /// <summary>
        /// Get the codex CLI command.
        /// </summary>
        protected override string GetCommand()
        {
            return ResolveExecutable(_ExecutablePath);
        }

        /// <summary>
        /// Build Codex CLI arguments.
        /// </summary>
        protected override List<string> BuildArguments(
            string workingDirectory,
            string prompt,
            string? model,
            string? finalMessageFilePath,
            Captain? captain)
        {
            List<string> args = new List<string>();

            args.Add("exec");
            args.Add("--json");
            if (String.Equals(ApprovalMode, "dangerous", StringComparison.OrdinalIgnoreCase))
            {
                args.Add("--dangerously-bypass-approvals-and-sandbox");
            }
            else if (String.Equals(ApprovalMode, "full-auto", StringComparison.OrdinalIgnoreCase)
                && (OperatingSystem.IsWindows() || OperatingSystem.IsLinux()))
            {
                // Armada already confines captains to an isolated dock worktree. In Linux
                // containers Codex's nested bubblewrap sandbox can fail on user namespace
                // and loopback setup even when the captain itself is correctly isolated.
                args.Add("--dangerously-bypass-approvals-and-sandbox");
            }
            else if (String.Equals(ApprovalMode, "full-auto", StringComparison.OrdinalIgnoreCase))
            {
                args.Add("--full-auto");
            }

            if (!String.IsNullOrEmpty(model))
            {
                // A provider-prefixed model id resolves to the provider-facing id (the prefix is
                // Armada's selection namespace); a custom-endpoint captain keeps its id verbatim.
                ResolvedModelProvider? resolved = ModelProviderResolver.Resolve(captain, model, _ModelProviders);
                string effectiveModel = (resolved != null && !String.IsNullOrWhiteSpace(resolved.ApiModelId))
                    ? resolved.ApiModelId
                    : model;

                args.Add("--model");
                args.Add(effectiveModel);

                // Codex routes external providers through a --profile config layer, not env vars.
                if (resolved != null)
                {
                    args.Add("--profile");
                    args.Add(ProviderProfileName(resolved));
                }
            }

            // Forward per-captain reasoning effort to Codex CLI as a per-invocation
            // config override. Codex CLI accepts -c model_reasoning_effort=<value>
            // for low|medium|high. Position before --output-last-message and
            // the prompt argument so Codex parses it as a config flag rather than
            // part of the prompt text. Null reasoningEffort preserves existing args
            // exactly (regression guard for captains without RuntimeOptionsJson).
            string? reasoningEffort = CaptainRuntimeOptions.GetReasoningEffort(captain);
            if (!String.IsNullOrWhiteSpace(reasoningEffort))
            {
                args.Add("-c");
                args.Add("model_reasoning_effort=" + reasoningEffort.Trim().ToLowerInvariant());
            }

            if (!String.IsNullOrEmpty(finalMessageFilePath))
            {
                args.Add("--output-last-message");
                args.Add(finalMessageFilePath);
            }

            args.Add(prompt);

            return args;
        }

        /// <summary>
        /// Capture exact usage from Codex's terminal turn event.
        /// </summary>
        protected override void HandleRawOutputLine(int processId, string line)
        {
            CodexEvent? evt = Deserialize(line);
            if (evt == null || !String.Equals(evt.Type, "turn.completed", StringComparison.Ordinal) || evt.Usage == null)
                return;

            CodexUsage reported = evt.Usage;
            PublishTokenUsage(processId, new RuntimeTokenUsage
            {
                Source = "codex.turn.completed",
                InputTokens = NonNegative(reported.InputTokens),
                OutputTokens = NonNegative(reported.OutputTokens),
                ReasoningTokens = NonNegative(reported.ReasoningOutputTokens),
                CacheReadTokens = NonNegative(reported.CachedInputTokens),
                CacheWriteTokens = NonNegative(reported.CacheWriteInputTokens)
            });
        }

        /// <summary>
        /// Keep Codex JSONL out of mission logs while preserving assistant protocol markers.
        /// </summary>
        protected override string TransformOutputLine(string line)
        {
            CodexEvent? evt = Deserialize(line);
            if (evt == null)
                return line;

            if (String.Equals(evt.Type, "item.completed", StringComparison.Ordinal) && evt.Item != null)
                return RenderItem(evt.Item);

            // Event-level errors (provider/transport failures, quota exhaustion) carry the
            // payload on the event itself, not on an item. Persist the message so an operator
            // can tell "out of usage" from "broken" instead of seeing the bare event type.
            if (String.Equals(evt.Type, "error", StringComparison.Ordinal))
            {
                string? message = !String.IsNullOrEmpty(evt.Message) ? evt.Message : evt.Error?.Message;
                return String.IsNullOrEmpty(message)
                    ? "[ARMADA:ACTIVITY] codex error"
                    : "[ARMADA:ACTIVITY] codex error " + StructuredRuntimeLogFormatter.RedactSecretValues(
                        StructuredRuntimeLogFormatter.TruncateActivityText(message, StructuredRuntimeLogFormatter.CommandDetailLimit));
            }

            // Item lifecycle and thread/turn bookkeeping carry no operator value; the completed
            // item records the same work with its arguments. Any other event type keeps a generic
            // activity record so failures and new CLI events stay visible.
            if (String.Equals(evt.Type, "item.started", StringComparison.Ordinal) ||
                String.Equals(evt.Type, "item.updated", StringComparison.Ordinal) ||
                String.Equals(evt.Type, "thread.started", StringComparison.Ordinal) ||
                String.Equals(evt.Type, "turn.started", StringComparison.Ordinal) ||
                String.Equals(evt.Type, "turn.completed", StringComparison.Ordinal))
            {
                return String.Empty;
            }

            return "[ARMADA:ACTIVITY] codex " + (evt.Type ?? "event").Replace('.', ' ');
        }

        /// <summary>
        /// Render one completed Codex item. Agent prose is logged as text; every other item
        /// becomes a named activity record carrying its primary argument. Item output (aggregated
        /// command output, diffs) and reasoning are deliberately excluded -- output is unbounded
        /// and can carry secrets, and reasoning is private model deliberation that every runtime
        /// drops so the log reads the same everywhere.
        /// </summary>
        private string RenderItem(CodexItem item)
        {
            if (String.Equals(item.Type, "agent_message", StringComparison.Ordinal))
                return item.Text ?? String.Empty;

            if (String.Equals(item.Type, "reasoning", StringComparison.Ordinal))
                return String.Empty;

            if (String.Equals(item.Type, "command_execution", StringComparison.Ordinal))
                return StructuredRuntimeLogFormatter.BuildToolActivity(
                    "bash",
                    item.Command,
                    BuildCommandStatus(item),
                    WorkingDirectory);

            if (String.Equals(item.Type, "file_change", StringComparison.Ordinal))
                return StructuredRuntimeLogFormatter.BuildToolActivity(
                    "edit",
                    BuildFileChangeDetail(item),
                    item.Status,
                    WorkingDirectory);

            if (String.Equals(item.Type, "mcp_tool_call", StringComparison.Ordinal))
                return StructuredRuntimeLogFormatter.BuildToolActivity(
                    BuildMcpToolName(item),
                    null,
                    item.Status,
                    WorkingDirectory);

            if (String.Equals(item.Type, "web_search", StringComparison.Ordinal))
                return StructuredRuntimeLogFormatter.BuildToolActivity(
                    "web_search",
                    item.Query,
                    null,
                    WorkingDirectory);

            if (String.Equals(item.Type, "error", StringComparison.Ordinal))
                return String.IsNullOrEmpty(item.Message)
                    ? "[ARMADA:ACTIVITY] codex item error"
                    : "[ARMADA:ACTIVITY] codex item error " + StructuredRuntimeLogFormatter.RedactSecretValues(
                        StructuredRuntimeLogFormatter.TruncateActivityText(item.Message, StructuredRuntimeLogFormatter.CommandDetailLimit));

            return "[ARMADA:ACTIVITY] codex item " + (item.Type ?? "unknown").Replace('_', ' ');
        }

        /// <summary>
        /// Describe how a command run ended, preferring the exit code over the status word.
        /// </summary>
        private static string? BuildCommandStatus(CodexItem item)
        {
            if (item.ExitCode.HasValue)
                return "exit " + item.ExitCode.Value;

            return item.Status;
        }

        /// <summary>
        /// Render the changed paths of a file_change item without the diff body.
        /// </summary>
        private static string? BuildFileChangeDetail(CodexItem item)
        {
            if (item.Changes == null || item.Changes.Count == 0)
                return null;

            List<string> paths = new List<string>();
            foreach (CodexFileChange change in item.Changes)
            {
                if (!String.IsNullOrWhiteSpace(change.Path))
                    paths.Add(change.Path.Trim());
            }

            if (paths.Count == 0)
                return null;

            return StructuredRuntimeLogFormatter.TruncateActivityText(
                String.Join(", ", paths),
                StructuredRuntimeLogFormatter.CommandDetailLimit);
        }

        /// <summary>
        /// Qualify an MCP tool with its server so two servers exposing the same tool stay distinct.
        /// </summary>
        private static string BuildMcpToolName(CodexItem item)
        {
            if (String.IsNullOrWhiteSpace(item.Tool))
                return "mcp";

            return String.IsNullOrWhiteSpace(item.Server)
                ? item.Tool.Trim()
                : item.Server.Trim() + "." + item.Tool.Trim();
        }

        private static CodexEvent? Deserialize(string line)
        {
            try
            {
                return JsonSerializer.Deserialize<CodexEvent>(line);
            }
            catch
            {
                return null;
            }
        }

        private static long NonNegative(long? value)
        {
            return Math.Max(0, value ?? 0);
        }

        #endregion

        #region Private-Types

        private sealed class CodexEvent
        {
            [JsonPropertyName("type")]
            public string? Type { get; set; }

            [JsonPropertyName("item")]
            public CodexItem? Item { get; set; }

            [JsonPropertyName("usage")]
            public CodexUsage? Usage { get; set; }

            [JsonPropertyName("message")]
            public string? Message { get; set; }

            [JsonPropertyName("error")]
            public CodexErrorPayload? Error { get; set; }
        }

        private sealed class CodexErrorPayload
        {
            [JsonPropertyName("message")]
            public string? Message { get; set; }
        }

        private sealed class CodexItem
        {
            [JsonPropertyName("type")]
            public string? Type { get; set; }

            [JsonPropertyName("text")]
            public string? Text { get; set; }

            [JsonPropertyName("command")]
            public string? Command { get; set; }

            [JsonPropertyName("exit_code")]
            public int? ExitCode { get; set; }

            [JsonPropertyName("status")]
            public string? Status { get; set; }

            [JsonPropertyName("changes")]
            [JsonConverter(typeof(CodexFileChangeListConverter))]
            public List<CodexFileChange>? Changes { get; set; }

            [JsonPropertyName("server")]
            public string? Server { get; set; }

            [JsonPropertyName("tool")]
            public string? Tool { get; set; }

            [JsonPropertyName("query")]
            public string? Query { get; set; }

            [JsonPropertyName("message")]
            public string? Message { get; set; }
        }

        private sealed class CodexFileChange
        {
            [JsonPropertyName("path")]
            public string? Path { get; set; }

            [JsonPropertyName("kind")]
            public string? Kind { get; set; }
        }

        /// <summary>
        /// Reads the changed-file set whether Codex reports it as an array of change objects or as
        /// a path-keyed map. Any other shape yields no paths instead of failing the whole event,
        /// which would put the raw JSON line into the mission log.
        /// </summary>
        private sealed class CodexFileChangeListConverter : JsonConverter<List<CodexFileChange>?>
        {
            public override List<CodexFileChange>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            {
                if (reader.TokenType == JsonTokenType.StartArray)
                {
                    return JsonSerializer.Deserialize<List<CodexFileChange>>(ref reader, options);
                }

                if (reader.TokenType == JsonTokenType.StartObject)
                {
                    List<CodexFileChange> changes = new List<CodexFileChange>();
                    while (reader.Read())
                    {
                        if (reader.TokenType == JsonTokenType.EndObject) break;

                        if (reader.TokenType == JsonTokenType.PropertyName)
                        {
                            changes.Add(new CodexFileChange { Path = reader.GetString() });
                            reader.Read();
                            reader.Skip();
                        }
                    }
                    return changes;
                }

                reader.Skip();
                return null;
            }

            public override void Write(Utf8JsonWriter writer, List<CodexFileChange>? value, JsonSerializerOptions options)
            {
                throw new NotSupportedException("Codex file changes are read-only telemetry.");
            }
        }

        private sealed class CodexUsage
        {
            [JsonPropertyName("input_tokens")]
            public long? InputTokens { get; set; }

            [JsonPropertyName("cached_input_tokens")]
            public long? CachedInputTokens { get; set; }

            [JsonPropertyName("cache_write_input_tokens")]
            public long? CacheWriteInputTokens { get; set; }

            [JsonPropertyName("output_tokens")]
            public long? OutputTokens { get; set; }

            [JsonPropertyName("reasoning_output_tokens")]
            public long? ReasoningOutputTokens { get; set; }
        }

        #endregion
    }
}
