namespace Armada.Runtimes
{
    using Armada.Core.Models;
    using Armada.Core.Services;
    using System.Diagnostics;
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

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="logging">Logging module.</param>
        public CodexRuntime(LoggingModule logging) : base(logging)
        {
        }

        #endregion

        #region Private-Methods

        /// <summary>
        /// Codex receives its prompt as a CLI argument, not via stdin. Suppressing the stdin pipe
        /// prevents Codex from detecting a piped context and printing "Reading additional input
        /// from stdin..." to stderr on every invocation.
        /// </summary>
        protected override bool RedirectStdin => false;

        /// <summary>
        /// Codex exec streams its full working transcript on stderr. Suppressing only the
        /// mission-log file write keeps logs bounded; the final answer is captured via
        /// --output-last-message and echoed to the log on exit, while heartbeat/progress
        /// still receive stderr through OnOutputReceived.
        /// </summary>
        protected override bool WriteStderrToLogFile => false;

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

            // NOTE: deliberately NOT passing --json. With --json, Codex emits its session as a
            // JSONL event stream, which makes final-answer capture and progress parsing harder to
            // keep human-readable. Keep upstream plain text so --output-last-message captures the
            // final answer cleanly and progress/result markers remain plain-text-detectable. The
            // verbose stderr transcript is kept out of the mission-log file by
            // WriteStderrToLogFile=false, not by a Codex CLI output flag.
            if (String.Equals(ApprovalMode, "dangerous", StringComparison.OrdinalIgnoreCase))
            {
                args.Add("--dangerously-bypass-approvals-and-sandbox");
            }
            else if (OperatingSystem.IsWindows() && String.Equals(ApprovalMode, "full-auto", StringComparison.OrdinalIgnoreCase))
            {
                args.Add("--dangerously-bypass-approvals-and-sandbox");
            }
            else if (String.Equals(ApprovalMode, "full-auto", StringComparison.OrdinalIgnoreCase))
            {
                args.Add("--full-auto");
            }

            if (!String.IsNullOrEmpty(model))
            {
                args.Add("--model");
                args.Add(model);
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

        #endregion
    }
}
