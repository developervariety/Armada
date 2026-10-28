namespace Armada.Runtimes
{
    using System;
    using System.Collections.Generic;
    using Armada.Core.Models;
    using Armada.Core.Protocol;
    using Armada.Core.Services;
    using SyslogLogging;

    /// <summary>
    /// Agent runtime adapter for the pi coding agent CLI (pi.dev). pi runs headlessly in print mode with JSONL
    /// event output (<c>pi -p --mode json --no-session</c>), reads the prompt from stdin, addresses models as
    /// <c>provider/id</c>, and maps per-captain reasoning effort to <c>--thinking</c>. pi never prompts for
    /// permission: Bypass is its default behavior and Refuse removes the <c>bash</c> tool.
    /// </summary>
    public class PiRuntime : BaseAgentRuntime
    {
        #region Public-Members

        /// <summary>
        /// Runtime display name.
        /// </summary>
        public override string Name => "Pi";

        /// <summary>
        /// pi session resume is not wired through Armada (launches use <c>--no-session</c>).
        /// </summary>
        public override bool SupportsResume => false;

        /// <summary>
        /// Path to the pi CLI executable.
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
        /// Whether pi may run shell commands. pi never prompts, so when false (or when the captain's effective
        /// auto-approve is off, the Refuse policy) the launch adds <c>--exclude-tools bash</c>.
        /// </summary>
        public bool AutoApprove { get; set; } = true;

        #endregion

        #region Private-Members

        private string _ExecutablePath = "pi";
        private readonly PiFinalMessage _FinalMessage = new PiFinalMessage();
        private bool _ProviderErrorRaised = false;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="logging">Logging module.</param>
        public PiRuntime(LoggingModule logging) : base(logging)
        {
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Whether a captured output line is a pi <c>--mode json</c> protocol record (a single-line JSON object whose
        /// <c>type</c> is one of pi's event types, <see cref="PiStreamEvent.KnownTypes"/>), so callers can filter it out of
        /// human-readable output rather than leaking raw JSON. Mirrors <c>OpenCodeRuntime.IsProtocolEventLine</c>.
        /// </summary>
        /// <param name="line">Raw output line.</param>
        /// <returns>True when the line is a pi protocol record.</returns>
        public static bool IsProtocolEventLine(string? line)
        {
            return PiStreamEvent.TryParse(line, out PiStreamEvent? _);
        }

        /// <summary>
        /// Human-visible assistant reply text streamed by a pi record (a <c>text_delta</c>), or null for any other record
        /// (thinking, tool calls, usage, lifecycle). Deltas are fragments; callers join them.
        /// </summary>
        /// <param name="line">Raw protocol line.</param>
        /// <returns>The text fragment, or null.</returns>
        public static string? TryExtractAssistantText(string? line)
        {
            if (!PiStreamEvent.TryParse(line, out PiStreamEvent? evt) || evt == null) return null;
            return evt.TextDelta;
        }

        /// <summary>
        /// The host user's pi MCP configuration file: <c>$PI_CODING_AGENT_DIR/mcp.json</c> when that variable is set,
        /// otherwise <c>~/.pi/agent/mcp.json</c>.
        /// </summary>
        /// <returns>The path (the file may not exist).</returns>
        public static string GetHostMcpConfigPath()
        {
            string? agentDirectory = Environment.GetEnvironmentVariable("PI_CODING_AGENT_DIR");
            if (!String.IsNullOrWhiteSpace(agentDirectory)) return System.IO.Path.Combine(agentDirectory!, "mcp.json");
            return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pi", "agent", "mcp.json");
        }

        #endregion

        #region Private-Methods

        /// <summary>
        /// Read provider failures from the JSON stream (pi exits 0 in JSON mode on provider errors) and from stderr. Only
        /// the first stdout provider error of a launch is raised: pi reports a final failure both in <c>agent_end</c> and
        /// in <c>auto_retry_end</c>.
        /// </summary>
        /// <param name="line">One output line.</param>
        /// <param name="fromStdout">True for a stdout line.</param>
        /// <returns>The provider error, or null.</returns>
        protected override RuntimeProviderError? TryParseProviderError(string line, bool fromStdout)
        {
            if (!fromStdout) return RuntimeProviderErrorParser.TryParseCliStderrLine(line, "Pi");
            _FinalMessage.Observe(line);
            RuntimeProviderError? error = RuntimeProviderErrorParser.TryParsePiJsonLine(line);
            if (error == null || _ProviderErrorRaised) return null;
            _ProviderErrorRaised = true;
            return error;
        }

        /// <summary>
        /// Start a launch: reset the stream outcome and remember the final-message file.
        /// </summary>
        /// <param name="finalMessageFilePath">The launch's final-message file, or null.</param>
        protected override void BeginLaunch(string? finalMessageFilePath)
        {
            _ProviderErrorRaised = false;
            _FinalMessage.Begin(finalMessageFilePath);
        }

        /// <summary>
        /// Resolve the exit code from the stream: exit 0 becomes nonzero when the last assistant message stopped with
        /// error, aborted or length, or the stream ended before <c>agent_end</c>. On success the last assistant reply is
        /// written as the final message.
        /// </summary>
        /// <param name="exitCode">The process exit code, or null when unknown.</param>
        /// <returns>The resolved exit code.</returns>
        protected override int? ResolveExitCode(int? exitCode)
        {
            return _FinalMessage.Complete(exitCode);
        }

        /// <summary>
        /// The runtime this adapter drives.
        /// </summary>
        protected override Armada.Core.Enums.AgentRuntimeEnum RuntimeType => Armada.Core.Enums.AgentRuntimeEnum.Pi;

        /// <summary>
        /// Get the command to execute for this runtime.
        /// </summary>
        protected override string GetCommand()
        {
            return ResolveExecutable(_ExecutablePath);
        }

        /// <summary>
        /// Build pi CLI arguments for a headless print-mode run.
        /// </summary>
        protected override List<string> BuildArguments(
            string workingDirectory,
            string prompt,
            string? model,
            string? finalMessageFilePath,
            Captain? captain)
        {
            string? thinking = ReasoningEffortTranslator.ToPiThinkingLevel(captain?.ReasoningEffort);
            bool refuseShell = !(AutoApprove && CaptainRuntimeOptions.GetAutoApprove(captain));
            return PiCommandBuilder.BuildRunArguments(workingDirectory, prompt, model, thinking, refuseShell, false);
        }

        /// <summary>
        /// Read the host user's pi MCP configuration (<c>$PI_CODING_AGENT_DIR/mcp.json</c>, else <c>~/.pi/agent/mcp.json</c>)
        /// so the launch's project <c>.pi/mcp.json</c> can redefine the user's own Armada entries under the same names, and
        /// any <c>.pi/mcp.json</c> already in the working directory so its servers are kept and the file is restored after
        /// the launch. Nothing is modified here.
        /// </summary>
        /// <param name="request">The plan request to populate.</param>
        protected override void PopulateHostMcpConfiguration(CaptainThreadMcpPlanRequest request)
        {
            List<string> documents = new List<string>();
            string? host = TryReadHostFile(GetHostMcpConfigPath());
            if (!String.IsNullOrWhiteSpace(host)) documents.Add(host!);
            request.HostPiMcpConfigDocuments = documents;
            if (!String.IsNullOrWhiteSpace(request.WorkingDirectory))
                request.ExistingPiProjectMcpJson = TryReadHostFile(System.IO.Path.Combine(request.WorkingDirectory, PiProjectMcpConfig.RelativePath));
        }

        /// <summary>
        /// Deliver the prompt on stdin: with no positional message, 'pi -p' reads redirected stdin to EOF (verified on
        /// macOS with pi 1.4.0). Windows: UNVERIFIED (no Windows host was available when pi support was added); stdin
        /// avoids the cmd.exe multi-line-argument truncation that a positional prompt through an npm ".cmd" wrapper
        /// would hit. If stdin fails there, switch to pi's <c>@file</c> argument.
        /// </summary>
        protected override bool UsePromptStdin => true;

        #endregion
    }
}
