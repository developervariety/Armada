namespace Armada.Core.Harbor
{
    using System;
    /// <summary>
    /// Harbor-to-server event: a launched captain process has started, reporting the host process id the
    /// Harbor will track for liveness and termination.
    /// </summary>
    public class HarborStarted : HarborMessage
    {
        #region Public-Members

        /// <summary>
        /// Job identifier of the started process.
        /// </summary>
        public string JobId { get; set; } = string.Empty;

        /// <summary>
        /// Host process id, as tracked on the Harbor. Not an Admiral-side PID.
        /// </summary>
        public int ProcessId { get; set; } = 0;

        /// <summary>UTC process start time, used to prove the exact attempt after a reconnect.</summary>
        public DateTime? ProcessStartedUtc { get; set; }

        #endregion
    }
}
