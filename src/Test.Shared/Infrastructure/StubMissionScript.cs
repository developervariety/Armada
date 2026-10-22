namespace Test.Shared.Infrastructure
{
    using System.Collections.Generic;
    using Armada.Core.Models;

    /// <summary>
    /// The scripted outcome of one <see cref="StubCaptainRuntime"/> mission launch: the faults a real captain runtime
    /// (Claude-like, Codex-like, Mux-like, or a flaky local model) can produce, so dispatch and handoff can be exercised
    /// against each without a real CLI.
    /// </summary>
    public sealed class StubMissionScript
    {
        #region Public-Members

        /// <summary>
        /// Exit code the launch reports. Default 0.
        /// </summary>
        public int ExitCode { get; set; } = 0;

        /// <summary>
        /// Write and commit one file in the dock before exiting. Default true.
        /// </summary>
        public bool Commit { get; set; } = true;

        /// <summary>
        /// Stdout lines the launch writes (its transcript).
        /// </summary>
        public List<string> Lines { get; set; } = new List<string>();

        /// <summary>
        /// Text written to the final-message file, or null to write none (a runtime without a final artifact).
        /// </summary>
        public string? FinalMessage { get; set; } = null;

        /// <summary>
        /// Structured provider error raised before the exit, or null.
        /// </summary>
        public RuntimeProviderError? ProviderError { get; set; } = null;

        /// <summary>
        /// Exit immediately instead of living for the stub's minimum lifetime: a millisecond in-process runtime whose exit
        /// can race the launch being recorded. Default false.
        /// </summary>
        public bool ExitImmediately { get; set; } = false;

        #endregion
    }
}
