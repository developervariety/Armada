namespace Armada.Runtimes
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Text.Json;
    using System.Text.Json.Serialization;
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
    /// Connection parameters (BaseUrl, Username, Password, Agent) are resolved through
    /// the shared <see cref="OpenCodeConnection"/> so there is exactly one resolver --
    /// the same type the inference client uses.
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

        private readonly OpenCodeConnection _Connection;

        private readonly LoggingModule _Logging;

        private string _Header = "[OpenCodeRuntime] ";

        private bool _SawAssistantOutput;

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
            // (process exited 0 but no assistant content was ever streamed) is
            // surfaced as a warning instead of being mis-read as WorkProduced.
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
        /// Produces: run -m &lt;model&gt; --format json [--variant &lt;reasoningEffort&gt;]
        ///           [--agent &lt;agent&gt;]
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
        /// The agent value is pulled from the shared OpenCodeConnection (same resolver
        /// the inference client uses) -- never hardcoded here.
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
        /// assistant text, or the original line when the event carries no text content
        /// or is not valid JSON.
        ///
        /// WHY: opencode --format json wraps all assistant output in typed JSON event
        /// objects (e.g. {"type":"assistant","content":"..."}). If raw JSON lines were
        /// surfaced to subscribers, the ProgressParser regex anchored at ^ would never
        /// match [ARMADA:*] protocol markers embedded inside a JSON string, breaking
        /// the admiral's mission-status detection. Reducing each event to its text
        /// content restores the plain-text contract subscribers depend on.
        ///
        /// Non-parseable lines (progress bars, debug noise, blank lines) fall through
        /// unchanged so they are not silently dropped from the mission log.
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
                _SawAssistantOutput = true;
                return evt.Content!;
            }

            return line;
        }

        /// <summary>
        /// True when the event carries non-empty assistant text content. Tolerant of
        /// unknown <c>type</c> values: any event with non-empty Content is treated as
        /// assistant output rather than hard-failing on unrecognized shapes.
        /// </summary>
        private static bool IsAssistantContentEvent(OpenCodeEvent evt)
        {
            return evt != null && !String.IsNullOrEmpty(evt.Content);
        }

        /// <summary>
        /// Scan opencode --format json event lines and concatenate assistant Content in
        /// order. Returns true with the joined text when any assistant content was seen,
        /// or false with empty text when the stream is empty or step_start-only. Stateless
        /// and process-free so it is unit-testable in isolation.
        /// </summary>
        /// <param name="lines">The raw JSON event lines from opencode stdout.</param>
        /// <param name="text">Joined assistant text, or empty when none was found.</param>
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
                        builder.Append(evt.Content);
                        sawContent = true;
                    }
                }
            }

            text = sawContent ? builder.ToString() : String.Empty;
            return sawContent;
        }

        /// <summary>
        /// Surface an empty run: when the process exited but no assistant content was ever
        /// streamed, log a warning with the pid so the empty-output exit is NOT a silent
        /// success that the admiral mis-reads as WorkProduced.
        /// </summary>
        private void HandleProcessExited(int processId, int? exitCode)
        {
            if (!_SawAssistantOutput)
            {
                _Logging.Warn(_Header + "opencode process " + processId + " exited with no assistant output");
            }
        }

        #endregion

        #region Private-Types

        /// <summary>
        /// Strongly-typed DTO for a single opencode --format json stdout event.
        /// opencode emits objects like {"type":"assistant","content":"...text..."}.
        /// Only the fields needed for admiral protocol detection are mapped here;
        /// unknown fields are ignored by the deserializer.
        /// </summary>
        private sealed class OpenCodeEvent
        {
            /// <summary>
            /// Event type (e.g. "assistant", "tool_call", "done").
            /// </summary>
            [JsonPropertyName("type")]
            public string? Type { get; set; }

            /// <summary>
            /// Text content of the event, present on assistant-role events.
            /// </summary>
            [JsonPropertyName("content")]
            public string? Content { get; set; }
        }

        #endregion
    }
}
