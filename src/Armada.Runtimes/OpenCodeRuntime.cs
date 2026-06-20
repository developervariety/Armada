namespace Armada.Runtimes
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Text;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using System.Text.RegularExpressions;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using SyslogLogging;

    /// <summary>
    /// Agent runtime adapter for the OpenCode CLI.
    /// </summary>
    /// <remarks>
    /// The opencode CLI is invoked with <c>run -m &lt;model&gt; --format json ...</c>.
    /// Prompt is delivered via stdin (not as a CLI argument) to avoid the Windows
    /// cmd.exe ~8KB command-line length limit when long mission briefs are dispatched.
    ///
    /// Connection parameters and the captain coding agent are resolved through
    /// <see cref="OpenCodeConnection"/>.
    ///
    /// Windows install path: resolved via ARMADA_TEST_OPENCODE env override, then
    /// PATH/npm fallback via ResolveExecutable.
    /// </remarks>
    public class OpenCodeRuntime : BaseAgentRuntime
    {
        #region Public-Members

        /// <summary>
        /// Runtime display name.
        /// </summary>
        public override string Name => "OpenCode";

        /// <summary>
        /// OpenCode does not support session resume.
        /// </summary>
        public override bool SupportsResume => false;

        #endregion

        #region Private-Members

        private static readonly JsonSerializerOptions _JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        private const int _ToolOutputSnippetLimit = 200;

        private readonly OpenCodeConnection _Connection;

        private readonly LoggingModule _Logging;

        private string _Header = "[OpenCodeRuntime] ";

        private bool _SawContent;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="logging">Logging module.</param>
        /// <param name="connectionSettings">
        /// Optional OpenCode server settings. When null, defaults are used so existing
        /// factory tests remain green without requiring explicit settings.
        /// </param>
        public OpenCodeRuntime(LoggingModule logging, OpenCodeServerSettings? connectionSettings = null)
            : base(logging)
        {
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
            _Connection = new OpenCodeConnection(connectionSettings ?? new OpenCodeServerSettings());

            // Self-subscribe to the inherited process-exit event so an empty run
            // (process exited 0 but no assistant content or tool calls were streamed)
            // is surfaced as a warning instead of being mis-read as WorkProduced.
            // Subscribing here keeps BaseAgentRuntime untouched.
            OnProcessExited += HandleProcessExited;
        }

        #endregion

        #region Private-Methods

        /// <summary>
        /// Get the opencode CLI command. Resolution order:
        /// 1. ARMADA_TEST_OPENCODE env var (test shim override, avoids polluting system paths).
        /// 2. PATH/npm fallback via ResolveExecutable.
        /// </summary>
        protected override string GetCommand()
        {
            string? testOverride = Environment.GetEnvironmentVariable("ARMADA_TEST_OPENCODE");
            if (!String.IsNullOrEmpty(testOverride))
                return testOverride;

            return ResolveExecutable("opencode");
        }

        /// <summary>
        /// Build OpenCode CLI arguments.
        /// Produces: run -m &lt;model&gt; --format json --dangerously-skip-permissions
        ///           [--variant &lt;reasoningEffort&gt;] [--agent &lt;agent&gt;]
        ///
        /// Runs <c>opencode run</c> standalone: the captain path no longer attaches to a
        /// daemon. WHY: on opencode 1.17.7 an <c>--attach &lt;BaseUrl&gt;</c> invocation
        /// returns only a step_start event and never streams the assistant result, so the
        /// captain exits 0 with no real output. Standalone run creates its own session and
        /// returns the assistant result, so the <c>--attach</c> flag and the <c>-p</c>/<c>-u</c>
        /// Basic-auth credentials that only served the attached server are dropped.
        ///
        /// The prompt is NOT included here; it is written to stdin instead (see UsePromptStdin)
        /// to avoid the Windows cmd.exe ~8KB command-line length limit on long mission briefs.
        ///
        /// --format json makes opencode emit a structured JSON event stream on stdout.
        /// TransformOutputLine parses those events so that [ARMADA:*] protocol markers
        /// remain Contains-detectable by the admiral via plain-text substring matching.
        ///
        /// The agent value is pulled from OpenCodeConnection -- never hardcoded here.
        /// </summary>
        protected override List<string> BuildArguments(
            string workingDirectory,
            string prompt,
            string? model,
            string? finalMessageFilePath,
            Captain? captain)
        {
            List<string> args = new List<string>();

            args.Add("run");

            if (!String.IsNullOrEmpty(model))
            {
                args.Add("-m");
                args.Add(model);
            }

            args.Add("--format");
            args.Add("json");

            // Auto-approve any permission not explicitly denied so the dock's
            // external_directory access is not auto-rejected. This is the runtime
            // override that beats both opencode's broken Windows path-glob matcher
            // for external_directory and the non-interactive run-mode permission
            // preset (which a permissive config file has been reported not to
            // override). Added unconditionally for the standalone run path.
            args.Add("--dangerously-skip-permissions");

            // Forward per-captain reasoning effort as --variant when set.
            // Null/blank means the captain has no runtime options or the option
            // was not configured; omit the flag entirely (regression guard, same
            // pattern as CodexRuntime).
            string? reasoningEffort = CaptainRuntimeOptions.GetReasoningEffort(captain);
            if (!String.IsNullOrWhiteSpace(reasoningEffort))
            {
                args.Add("--variant");
                args.Add(reasoningEffort.Trim());
            }

            string resolvedAgent = _Connection.ResolveAgent();
            if (!String.IsNullOrWhiteSpace(resolvedAgent))
            {
                args.Add("--agent");
                args.Add(resolvedAgent);
            }

            return args;
        }

        /// <summary>
        /// OpenCode reads the prompt from stdin. Writing via stdin avoids the Windows
        /// cmd.exe ~8KB command-line length limit that would silently fail on long
        /// mission briefs passed as positional arguments.
        /// </summary>
        protected override bool UsePromptStdin => true;

        /// <summary>
        /// Parse a single opencode --format json stdout line and return the inner
        /// assistant text, a concise tool-call narration, empty text for recognized
        /// non-content events, or the original line when the line is not valid JSON.
        ///
        /// WHY: opencode --format json wraps all assistant output in typed JSON event
        /// objects with nested text parts. If raw JSON lines were
        /// surfaced to subscribers, the ProgressParser regex anchored at ^ would never
        /// match [ARMADA:*] protocol markers embedded inside a JSON string, breaking
        /// the admiral's mission-status detection. Reducing each event to its text
        /// content restores the plain-text contract subscribers depend on.
        ///
        /// The build agent frequently emits <c>tool_use</c> events before (or instead of)
        /// assistant text. Those events are reduced to a concise <c>[tool: name]</c> line
        /// so an operator can follow what the captain did without leaking raw JSON or full
        /// tool output into the mission log.
        ///
        /// Non-parseable lines (progress bars, debug noise, blank lines) fall through
        /// unchanged so they are not silently dropped from the mission log. Recognized
        /// structured noise is suppressed so raw JSON does not leak into the log file.
        /// </summary>
        protected override string TransformOutputLine(string line)
        {
            if (String.IsNullOrEmpty(line))
                return line;

            OpenCodeEvent? evt = null;
            try
            {
                evt = JsonSerializer.Deserialize<OpenCodeEvent>(line, _JsonOptions);
            }
            catch
            {
                // Non-JSON line: fall through and return the line as-is.
            }

            if (evt != null && IsAssistantContentEvent(evt))
            {
                _SawContent = true;
                return BuildAssistantContent(evt);
            }

            if (evt != null && IsToolUseEvent(evt))
            {
                _SawContent = true;
                return BuildToolNarration(evt);
            }

            if (evt != null && IsRecognizedNonContentEvent(evt))
            {
                return string.Empty;
            }

            return line;
        }

        /// <summary>
        /// True when the event carries non-empty assistant text content.
        /// </summary>
        private static bool IsAssistantContentEvent(OpenCodeEvent evt)
        {
            return evt != null
                && evt.Part != null
                && (IsTextContentEvent(evt) || IsReasoningContentEvent(evt))
                && !String.IsNullOrEmpty(evt.Part.Text);
        }

        /// <summary>
        /// True when the event is a normal assistant text event.
        /// </summary>
        private static bool IsTextContentEvent(OpenCodeEvent evt)
        {
            return String.Equals(evt.Type, "text", StringComparison.Ordinal)
                && evt.Part != null
                && String.Equals(evt.Part.Type, "text", StringComparison.Ordinal);
        }

        /// <summary>
        /// True when the event is an assistant reasoning event.
        /// </summary>
        private static bool IsReasoningContentEvent(OpenCodeEvent evt)
        {
            return String.Equals(evt.Type, "reasoning", StringComparison.Ordinal)
                || (evt.Part != null && String.Equals(evt.Part.Type, "reasoning", StringComparison.Ordinal));
        }

        /// <summary>
        /// Build assistant content for the mission log.
        /// </summary>
        private static string BuildAssistantContent(OpenCodeEvent evt)
        {
            string text = evt.Part!.Text!;
            return IsReasoningContentEvent(evt) ? RedactSecretValues(text) : text;
        }

        /// <summary>
        /// True when the event is a tool_use event with a usable tool name.
        /// </summary>
        private static bool IsToolUseEvent(OpenCodeEvent evt)
        {
            return evt != null
                && String.Equals(evt.Type, "tool_use", StringComparison.Ordinal)
                && evt.Part != null
                && String.Equals(evt.Part.Type, "tool", StringComparison.Ordinal)
                && !String.IsNullOrEmpty(evt.Part.Tool);
        }

        /// <summary>
        /// Build a concise, secret-safe plaintext narration of a tool_use event for the mission log.
        /// </summary>
        private static string BuildToolNarration(OpenCodeEvent evt)
        {
            OpenCodeEventPart part = evt.Part!;
            string tool = part.Tool ?? "unknown";
            StringBuilder builder = new StringBuilder();
            builder.Append("[tool: ");
            builder.Append(tool);
            builder.Append("]");

            string summary = BuildToolArgSummary(tool, part.State?.Input);
            if (!String.IsNullOrEmpty(summary))
            {
                builder.Append(" ");
                builder.Append(summary);
            }

            string snippet = BuildToolOutputSnippet(part.State?.Output);
            if (!String.IsNullOrEmpty(snippet))
            {
                builder.Append(" -> ");
                builder.Append(snippet);
            }

            return builder.ToString();
        }

        /// <summary>
        /// Build a compact argument summary for the tool types OpenCode emits.
        /// </summary>
        private static string BuildToolArgSummary(string tool, OpenCodeToolInput? input)
        {
            if (input == null)
            {
                return String.Empty;
            }

            if (String.Equals(tool, "read", StringComparison.Ordinal))
            {
                return NormalizeOneLine(input.FilePath);
            }

            if (String.Equals(tool, "bash", StringComparison.Ordinal))
            {
                return NormalizeOneLine(input.Command);
            }

            if (String.Equals(tool, "grep", StringComparison.Ordinal))
            {
                string pattern = NormalizeOneLine(input.Pattern);
                string path = NormalizeOneLine(input.Path);
                if (String.IsNullOrEmpty(path))
                {
                    path = NormalizeOneLine(input.Include);
                }

                if (!String.IsNullOrEmpty(pattern) && !String.IsNullOrEmpty(path))
                {
                    return "\"" + pattern + "\" in " + path;
                }

                return !String.IsNullOrEmpty(pattern) ? "\"" + pattern + "\"" : path;
            }

            if (String.Equals(tool, "glob", StringComparison.Ordinal))
            {
                return NormalizeOneLine(input.Pattern);
            }

            if (String.Equals(tool, "todowrite", StringComparison.Ordinal))
            {
                int count = input.Todos?.Count ?? input.Items?.Count ?? 0;
                return count.ToString() + " items";
            }

            return BuildUnknownToolArgSummary(input);
        }

        /// <summary>
        /// Build a best-effort argument summary for future or unknown tool names.
        /// </summary>
        private static string BuildUnknownToolArgSummary(OpenCodeToolInput input)
        {
            List<string> parts = new List<string>();
            AddKnownSummary(parts, "filePath", NormalizeOneLine(input.FilePath), false);
            AddKnownSummary(parts, "command", NormalizeOneLine(input.Command), false);
            AddKnownSummary(parts, "pattern", NormalizeOneLine(input.Pattern), false);
            AddKnownSummary(parts, "path", NormalizeOneLine(input.Path), false);
            AddKnownSummary(parts, "include", NormalizeOneLine(input.Include), false);
            AddKnownSummary(parts, "token", NormalizeOneLine(input.Token), true);
            AddKnownSummary(parts, "password", NormalizeOneLine(input.Password), true);
            AddKnownSummary(parts, "secret", NormalizeOneLine(input.Secret), true);
            AddKnownSummary(parts, "key", NormalizeOneLine(input.Key), true);
            AddKnownSummary(parts, "seed", NormalizeOneLine(input.Seed), true);
            AddKnownSummary(parts, "privateKey", NormalizeOneLine(input.PrivateKey), true);

            if (input.Todos != null)
            {
                parts.Add("todos=" + input.Todos.Count + " items");
            }
            else if (input.Items != null)
            {
                parts.Add("items=" + input.Items.Count + " items");
            }

            return String.Join(", ", parts);
        }

        /// <summary>
        /// Add a known field to an unknown-tool summary.
        /// </summary>
        private static void AddKnownSummary(List<string> parts, string name, string value, bool sensitive)
        {
            if (String.IsNullOrEmpty(value))
            {
                return;
            }

            string safeValue = sensitive ? RedactByFieldName(value) : RedactSecretValues(value);
            parts.Add(name + "=" + safeValue);
        }

        /// <summary>
        /// Build a bounded single-line tool output snippet.
        /// </summary>
        private static string BuildToolOutputSnippet(string? output)
        {
            string normalized = RedactSecretValues(NormalizeOneLine(output));
            if (String.IsNullOrEmpty(normalized))
            {
                return String.Empty;
            }

            if (normalized.Length <= _ToolOutputSnippetLimit)
            {
                return normalized;
            }

            return normalized.Substring(0, _ToolOutputSnippetLimit) + " [truncated]";
        }

        /// <summary>
        /// Normalize streamed text to one log line.
        /// </summary>
        private static string NormalizeOneLine(string? value)
        {
            if (String.IsNullOrEmpty(value))
            {
                return String.Empty;
            }

            return value.Replace("\r", " ").Replace("\n", " ").Trim();
        }

        /// <summary>
        /// Redact values whose field names are already known to be sensitive.
        /// </summary>
        private static string RedactByFieldName(string value)
        {
            return "<redacted len=" + value.Length + ">";
        }

        /// <summary>
        /// Redact token/password/key-shaped material while preserving structural text.
        /// </summary>
        private static string RedactSecretValues(string value)
        {
            if (String.IsNullOrEmpty(value))
            {
                return String.Empty;
            }

            string redacted = Regex.Replace(
                value,
                "(?i)(password|token|secret|seed|private[_-]?key|api[_-]?key)\\s*[:=]\\s*([^\\s,;]+)",
                RedactNamedSecret);

            redacted = Regex.Replace(
                redacted,
                "-----BEGIN [A-Z ]*PRIVATE KEY-----.*?-----END [A-Z ]*PRIVATE KEY-----",
                RedactMatchedSecret,
                RegexOptions.Singleline);

            redacted = Regex.Replace(
                redacted,
                "\\b[0-9a-fA-F]{32,}\\b",
                RedactMatchedSecret);

            redacted = Regex.Replace(
                redacted,
                "\\b[A-Za-z0-9+/]{40,}={0,2}\\b",
                RedactMatchedSecret);

            return redacted;
        }

        /// <summary>
        /// Regex evaluator for key=value secret values.
        /// </summary>
        private static string RedactNamedSecret(Match match)
        {
            return match.Groups[1].Value + "=<redacted len=" + match.Groups[2].Value.Length + ">";
        }

        /// <summary>
        /// Regex evaluator for standalone secret-looking values.
        /// </summary>
        private static string RedactMatchedSecret(Match match)
        {
            return "<redacted len=" + match.Value.Length + ">";
        }

        /// <summary>
        /// True when the event is structured OpenCode noise that should not be logged raw.
        /// tool_use is NOT noise: it is narrated by <see cref="BuildToolNarration"/>. Other
        /// tool-prefixed events (tool_call, etc.) remain suppressed as structured noise.
        /// </summary>
        private static bool IsRecognizedNonContentEvent(OpenCodeEvent evt)
        {
            if (evt == null || String.IsNullOrEmpty(evt.Type))
            {
                return false;
            }

            return String.Equals(evt.Type, "step_start", StringComparison.Ordinal)
                || String.Equals(evt.Type, "step_finish", StringComparison.Ordinal)
                || String.Equals(evt.Type, "step-start", StringComparison.Ordinal)
                || String.Equals(evt.Type, "step-finish", StringComparison.Ordinal)
                || (evt.Type.StartsWith("tool", StringComparison.Ordinal) && !String.Equals(evt.Type, "tool_use", StringComparison.Ordinal));
        }

        /// <summary>
        /// Scan opencode --format json event lines and concatenate assistant text parts and
        /// tool-call narrations in order. Returns true with the joined text when any content
        /// was seen, or false with empty text when the stream is empty or step_start-only.
        /// Stateless and process-free so it is unit-testable in isolation.
        /// </summary>
        /// <param name="lines">The raw JSON event lines from opencode stdout.</param>
        /// <param name="text">Joined assistant text and tool narrations, or empty when none was found.</param>
        protected bool TryExtractAssistantResult(IReadOnlyList<string> lines, out string text)
        {
            System.Text.StringBuilder builder = new System.Text.StringBuilder();
            bool sawContent = false;

            if (lines != null)
            {
                foreach (string line in lines)
                {
                    if (String.IsNullOrEmpty(line))
                        continue;

                    OpenCodeEvent? evt = null;
                    try
                    {
                        evt = JsonSerializer.Deserialize<OpenCodeEvent>(line, _JsonOptions);
                    }
                    catch
                    {
                        // Non-JSON noise line: ignore for assistant-result extraction.
                    }

                    if (evt != null && IsAssistantContentEvent(evt))
                    {
                        builder.Append(BuildAssistantContent(evt));
                        sawContent = true;
                    }
                    else if (evt != null && IsToolUseEvent(evt))
                    {
                        builder.Append(BuildToolNarration(evt));
                        sawContent = true;
                    }
                }
            }

            text = sawContent ? builder.ToString() : String.Empty;
            return sawContent;
        }

        /// <summary>
        /// Surface an empty run: when the process exited but no assistant content or tool
        /// calls were ever streamed, log a warning with the pid so the empty-output exit is
        /// NOT a silent success that the admiral mis-reads as WorkProduced.
        /// </summary>
        private void HandleProcessExited(int processId, int? exitCode)
        {
            if (!_SawContent)
            {
                _Logging.Warn(_Header + "opencode process " + processId + " exited with no streamed content");
            }
        }

        #endregion

        #region Private-Types

        /// <summary>
        /// Strongly-typed DTO for a single opencode --format json stdout event.
        /// opencode emits text objects with nested part.text assistant content.
        /// Only the fields needed for admiral protocol detection are mapped here;
        /// unknown fields are ignored by the deserializer.
        /// </summary>
        private sealed class OpenCodeEvent
        {
            /// <summary>
            /// Event type (e.g. "text", "step_start", "tool_call").
            /// </summary>
            [JsonPropertyName("type")]
            public string? Type { get; set; }

            /// <summary>
            /// Nested event part, present on assistant text events.
            /// </summary>
            [JsonPropertyName("part")]
            public OpenCodeEventPart? Part { get; set; }
        }

        /// <summary>
        /// Nested part payload for OpenCode text and tool_use events.
        /// </summary>
        private sealed class OpenCodeEventPart
        {
            /// <summary>
            /// Part type (e.g. "text", "tool").
            /// </summary>
            [JsonPropertyName("type")]
            public string? Type { get; set; }

            /// <summary>
            /// Assistant text content.
            /// </summary>
            [JsonPropertyName("text")]
            public string? Text { get; set; }

            /// <summary>
            /// Tool name for tool_use events (e.g. "read", "bash").
            /// </summary>
            [JsonPropertyName("tool")]
            public string? Tool { get; set; }

            /// <summary>
            /// Tool call identifier for tool_use events.
            /// </summary>
            [JsonPropertyName("callID")]
            public string? CallId { get; set; }

            /// <summary>
            /// Tool execution state for tool_use events.
            /// </summary>
            [JsonPropertyName("state")]
            public OpenCodeToolState? State { get; set; }
        }

        /// <summary>
        /// Tool execution state carried on tool_use event parts.
        /// </summary>
        private sealed class OpenCodeToolState
        {
            /// <summary>
            /// Execution status (e.g. "completed", "failed").
            /// </summary>
            [JsonPropertyName("status")]
            public string? Status { get; set; }

            /// <summary>
            /// Structured input arguments for the tool invocation.
            /// </summary>
            [JsonPropertyName("input")]
            public OpenCodeToolInput? Input { get; set; }

            /// <summary>
            /// Tool output captured by OpenCode after execution.
            /// </summary>
            [JsonPropertyName("output")]
            public string? Output { get; set; }
        }

        /// <summary>
        /// Strongly-typed DTO for OpenCode tool input fields used in narration.
        /// </summary>
        private sealed class OpenCodeToolInput
        {
            /// <summary>
            /// File path used by read-like tools.
            /// </summary>
            [JsonPropertyName("filePath")]
            public string? FilePath { get; set; }

            /// <summary>
            /// Command used by bash-like tools.
            /// </summary>
            [JsonPropertyName("command")]
            public string? Command { get; set; }

            /// <summary>
            /// Pattern used by grep and glob tools.
            /// </summary>
            [JsonPropertyName("pattern")]
            public string? Pattern { get; set; }

            /// <summary>
            /// Search path used by grep-like tools.
            /// </summary>
            [JsonPropertyName("path")]
            public string? Path { get; set; }

            /// <summary>
            /// Include selector used by grep-like tools.
            /// </summary>
            [JsonPropertyName("include")]
            public string? Include { get; set; }

            /// <summary>
            /// Todo collection used by todowrite.
            /// </summary>
            [JsonPropertyName("todos")]
            public List<OpenCodeTodoItem>? Todos { get; set; }

            /// <summary>
            /// Alternate item collection used by todo-like tools.
            /// </summary>
            [JsonPropertyName("items")]
            public List<OpenCodeTodoItem>? Items { get; set; }

            /// <summary>
            /// Token value for unknown-tool fallback redaction.
            /// </summary>
            [JsonPropertyName("token")]
            public string? Token { get; set; }

            /// <summary>
            /// Password value for unknown-tool fallback redaction.
            /// </summary>
            [JsonPropertyName("password")]
            public string? Password { get; set; }

            /// <summary>
            /// Secret value for unknown-tool fallback redaction.
            /// </summary>
            [JsonPropertyName("secret")]
            public string? Secret { get; set; }

            /// <summary>
            /// Key value for unknown-tool fallback redaction.
            /// </summary>
            [JsonPropertyName("key")]
            public string? Key { get; set; }

            /// <summary>
            /// Seed value for unknown-tool fallback redaction.
            /// </summary>
            [JsonPropertyName("seed")]
            public string? Seed { get; set; }

            /// <summary>
            /// Private key value for unknown-tool fallback redaction.
            /// </summary>
            [JsonPropertyName("privateKey")]
            public string? PrivateKey { get; set; }
        }

        /// <summary>
        /// Minimal todo DTO used only for item counts.
        /// </summary>
        private sealed class OpenCodeTodoItem
        {
            /// <summary>
            /// Todo content, intentionally not logged.
            /// </summary>
            [JsonPropertyName("content")]
            public string? Content { get; set; }
        }

        #endregion
    }
}
