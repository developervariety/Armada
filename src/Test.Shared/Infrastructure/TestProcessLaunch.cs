namespace Test.Shared.Infrastructure
{
    using System;
    using Armada.Core.Enums;

    /// <summary>
    /// One agent start a test host observed through <see cref="TestAgentRuntimeFactory"/>.
    /// </summary>
    public sealed class TestProcessLaunch
    {
        #region Public-Members

        /// <summary>
        /// Runtime the captain was configured with.
        /// </summary>
        public AgentRuntimeEnum RuntimeType { get; set; }

        /// <summary>
        /// True when an operating-system process was started. False for the non-launching test runtime.
        /// </summary>
        public bool LaunchedProcess { get; set; }

        /// <summary>
        /// Process identifier the runtime reported. Synthetic for the non-launching test runtime.
        /// </summary>
        public int ProcessId { get; set; }

        /// <summary>
        /// Executable name of a started process, or the reason it could not be read.
        /// </summary>
        public string? ProcessName { get; set; }

        /// <summary>
        /// Captain the start was made for, when the caller supplied one.
        /// </summary>
        public string? CaptainId { get; set; }

        /// <summary>
        /// Exit code reported for the start, or null while it runs or when none was reported.
        /// </summary>
        public int? ExitCode { get; set; }

        /// <summary>
        /// Time the start was recorded.
        /// </summary>
        public DateTime StartedUtc { get; set; } = DateTime.UtcNow;

        #endregion

        #region Public-Methods

        /// <summary>
        /// One-line description naming the runtime, the process and its exit code.
        /// </summary>
        /// <returns>Description.</returns>
        public override string ToString()
        {
            return RuntimeType
                + (LaunchedProcess ? " process '" + (ProcessName ?? "unknown") + "'" : " test runtime")
                + " pid " + ProcessId
                + (String.IsNullOrEmpty(CaptainId) ? "" : " captain " + CaptainId)
                + " exit " + (ExitCode.HasValue ? ExitCode.Value.ToString() : "none");
        }

        #endregion
    }
}
