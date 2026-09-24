namespace Armada.Core.Services
{
    using System;

    /// <summary>
    /// Outcome of one <see cref="BoundedProcessRunner"/> run. Nothing is dropped silently: a timeout, a caller
    /// cancellation, a truncated stream, a pipe still held open after exit, and a failed kill each have their own
    /// field.
    /// </summary>
    public sealed class BoundedProcessResult
    {
        #region Public-Members

        /// <summary>Exit code; null when the process was killed for a timeout or a cancellation.</summary>
        public int? ExitCode { get; set; } = null;

        /// <summary>Kept standard output, or the combined output when the request combined the streams.</summary>
        public string StandardOutput { get; set; } = String.Empty;

        /// <summary>Kept standard error; empty when the request combined the streams.</summary>
        public string StandardError { get; set; } = String.Empty;

        /// <summary>Whether standard output lost bytes to its budget.</summary>
        public bool StandardOutputTruncated { get; set; } = false;

        /// <summary>UTF-8 bytes of standard output dropped.</summary>
        public long StandardOutputOmittedBytes { get; set; } = 0;

        /// <summary>Whether standard error lost bytes to its budget.</summary>
        public bool StandardErrorTruncated { get; set; } = false;

        /// <summary>UTF-8 bytes of standard error dropped.</summary>
        public long StandardErrorOmittedBytes { get; set; } = 0;

        /// <summary>Whether the process ran past its timeout and was killed.</summary>
        public bool TimedOut { get; set; } = false;

        /// <summary>Whether the caller's token was cancelled and the process was killed.</summary>
        public bool Cancelled { get; set; } = false;

        /// <summary>
        /// Whether an output pipe was still open when its drain window ended, because a child the process started
        /// still held it. The readers stopped there; output written after that is not in the result.
        /// </summary>
        public bool OutputDrainTimedOut { get; set; } = false;

        /// <summary>Why the kill failed; null when no kill was needed or it succeeded.</summary>
        public string? KillError { get; set; } = null;

        /// <summary>Whether the process was still running when the post-kill wait ended.</summary>
        public bool StillRunningAfterKill { get; set; } = false;

        /// <summary>
        /// Descendants killed after the group and tree kill because they carried the run identifier, having left both
        /// the process tree and the process group. Linux group-owning runs only; zero elsewhere.
        /// </summary>
        public int EscapedProcessesKilled { get; set; } = 0;

        /// <summary>
        /// Processes of the same user whose environment the kernel refused to show during the containment sweep, so
        /// they could not be checked for the run identifier.
        /// </summary>
        public int ContainmentUnreadableProcesses { get; set; } = 0;

        /// <summary>Why the containment sweep stopped before it was sure it had found every descendant; null when it finished or did not run.</summary>
        public string? ContainmentSweepError { get; set; } = null;

        /// <summary>Why writing standard input failed; null when it succeeded or there was none.</summary>
        public string? StandardInputError { get; set; } = null;

        /// <summary>Time from start to the end of the run.</summary>
        public TimeSpan Duration { get; set; } = TimeSpan.Zero;

        /// <summary>Whether either stream lost bytes to its budget.</summary>
        public bool Truncated => StandardOutputTruncated || StandardErrorTruncated;

        /// <summary>Whether the process exited on its own with code 0.</summary>
        public bool Succeeded => !TimedOut && !Cancelled && ExitCode == 0;

        #endregion
    }
}
