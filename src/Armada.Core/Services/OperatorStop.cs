namespace Armada.Core.Services
{
    using System;
    using Armada.Core.Models;

    /// <summary>
    /// The one rule for a mission an operator stopped on purpose. A stop from REST, MCP or WebSocket ends the
    /// running mission Cancelled with a reason that starts with <see cref="FailureReasonPrefix"/>, so recovery,
    /// the inbox failure feed and failure counts can tell a deliberate stop from a fault.
    /// </summary>
    public static class OperatorStop
    {
        #region Public-Members

        /// <summary>The failure reason prefix of a mission an operator stopped.</summary>
        public const string FailureReasonPrefix = "operator_stopped:";

        /// <summary>Event type recorded when an operator stop ends a mission.</summary>
        public const string StoppedEventType = "mission.stopped_by_operator";

        /// <summary>Event type the dock reclaim records when it saves a dock's uncommitted work.</summary>
        public const string WorkSnapshotSavedEventType = "mission.work_snapshot_saved";

        #endregion

        #region Public-Methods

        /// <summary>
        /// True when a failure reason marks a mission an operator stopped.
        /// </summary>
        /// <param name="failureReason">Recorded failure reason.</param>
        /// <returns>True when the reason starts with <see cref="FailureReasonPrefix"/>.</returns>
        public static bool IsOperatorStopped(string? failureReason)
        {
            return !String.IsNullOrEmpty(failureReason)
                && failureReason.StartsWith(FailureReasonPrefix, StringComparison.Ordinal);
        }

        /// <summary>
        /// Build the failure reason for a stopped mission: who stopped it, from which surface, and when (UTC).
        /// </summary>
        /// <param name="context">Who stopped the mission and from where.</param>
        /// <param name="stoppedUtc">Stop time.</param>
        /// <param name="snapshotRef">Recover ref that holds the dock's uncommitted work, or null when none was written.</param>
        /// <returns>The reason.</returns>
        public static string BuildReason(OperatorStopContext context, DateTime stoppedUtc, string? snapshotRef)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            string reason = FailureReasonPrefix + " stopped by " + context.Who + " via " + context.Surface
                + " at " + stoppedUtc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
            if (!String.IsNullOrWhiteSpace(snapshotRef))
                reason += "; uncommitted work saved to " + snapshotRef;
            return reason;
        }

        #endregion
    }
}
