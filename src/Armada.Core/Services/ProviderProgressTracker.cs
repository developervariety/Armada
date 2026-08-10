namespace Armada.Core.Services
{
    using System;
    using System.Collections.Concurrent;

    /// <summary>
    /// In-memory tracker of the last provider-progress timestamp per captain.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Provider-progress is a strictly narrower signal than captain-heartbeat. The captain
    /// heartbeat advances on every line of stdout or stderr (progress bars, tool-call
    /// activity records, debug noise) so a captain whose process is alive never stops
    /// emitting heartbeats. Provider-progress advances only when the agent's underlying
    /// provider has demonstrably made forward motion on a request -- in OpenCode today
    /// that is a <c>step_finish</c> event with non-null token usage. A captain whose
    /// provider request has silently hung inside the model provider keeps the OS
    /// process alive, keeps emitting heartbeats, and so never trips the existing
    /// heartbeat-based Mail nudge. The tracker exists so the recovery orchestrator
    /// can distinguish a captain-wide heartbeat stall from a provider-silent stall
    /// and bound the latter within the configured stall window.
    /// </para>
    /// <para>
    /// The tracker is in-memory only. It is intentionally not persisted: a captain
    /// that has never emitted a provider-progress event in this Admiral process has
    /// no historical baseline to restore, and the recovery orchestrator treats
    /// "never recorded" identically to "recorded long ago". Restarting the Admiral
    /// resets the tracker and so re-classifies freshly started captains as
    /// <see cref="ProviderStallKind.None"/> until the runtime publishes its first
    /// provider-progress event.
    /// </para>
    /// </remarks>
    public sealed class ProviderProgressTracker
    {
        #region Private-Members

        private readonly ConcurrentDictionary<string, DateTime> _LastProgressUtc = new ConcurrentDictionary<string, DateTime>(StringComparer.Ordinal);

        #endregion

        #region Public-Methods

        /// <summary>
        /// Record a provider-progress event for the given captain.
        /// </summary>
        /// <param name="captainId">Captain identifier.</param>
        /// <param name="utc">UTC instant the provider produced forward progress.</param>
        public void Record(string captainId, DateTime utc)
        {
            if (String.IsNullOrWhiteSpace(captainId)) return;
            DateTime normalized = utc.Kind == DateTimeKind.Utc ? utc : utc.ToUniversalTime();
            _LastProgressUtc[captainId] = normalized;
        }

        /// <summary>
        /// Look up the last recorded provider-progress timestamp for the given captain.
        /// </summary>
        /// <param name="captainId">Captain identifier.</param>
        /// <param name="lastProgressUtc">Last recorded provider-progress timestamp, or null if the captain has never been recorded.</param>
        /// <returns>True when a timestamp was recorded; false otherwise.</returns>
        public bool TryGet(string captainId, out DateTime? lastProgressUtc)
        {
            lastProgressUtc = null;
            if (String.IsNullOrWhiteSpace(captainId)) return false;
            if (!_LastProgressUtc.TryGetValue(captainId, out DateTime value)) return false;
            lastProgressUtc = value;
            return true;
        }

        /// <summary>
        /// Clear any recorded provider-progress for the given captain. Call this when a
        /// captain is recalled, retired, or transitions to a terminal mission status so
        /// the tracker does not leak entries across mission boundaries.
        /// </summary>
        /// <param name="captainId">Captain identifier.</param>
        public void Clear(string captainId)
        {
            if (String.IsNullOrWhiteSpace(captainId)) return;
            _LastProgressUtc.TryRemove(captainId, out _);
        }

        #endregion
    }

    /// <summary>
    /// Classification of a captain stall by source.
    /// </summary>
    public enum ProviderStallKind
    {
        /// <summary>
        /// No stall. Both heartbeat and provider-progress are recent, or the captain
        /// has not yet produced a measurement.
        /// </summary>
        None = 0,

        /// <summary>
        /// The captain's OS process is alive but the heartbeat has not been refreshed
        /// within the stall window. The captain has not produced any output at all.
        /// </summary>
        HeartbeatStall = 1,

        /// <summary>
        /// The captain's heartbeat is fresh (process is emitting output) but the
        /// provider-progress signal is stale. The OS process is alive and stdout is
        /// flowing (progress bars, tool-call activity, debug noise) but the underlying
        /// provider request has not made forward motion. This is the silent-provider
        /// stall the objective targets.
        /// </summary>
        ProviderSilentStall = 2,

        /// <summary>
        /// Both heartbeat and provider-progress are stale past the threshold. The
        /// captain is producing no output AND its provider has not advanced.
        /// </summary>
        HeartbeatAndProviderStall = 3,
    }

    /// <summary>
    /// Pure helper that classifies a captain's stall state by source.
    /// </summary>
    public static class ProviderStallClassifier
    {
        /// <summary>
        /// Classify the stall state implied by a captain's last heartbeat and last
        /// provider-progress timestamp.
        /// </summary>
        /// <param name="lastHeartbeatUtc">Captain heartbeat timestamp, or null when no heartbeat has been recorded.</param>
        /// <param name="lastProviderProgressUtc">Provider-progress timestamp, or null when no provider-progress has been recorded.</param>
        /// <param name="thresholdMinutes">Stall threshold in minutes. Must be positive; values &lt;= 0 are treated as 1 minute to keep the function total.</param>
        /// <param name="nowUtc">Reference "now" timestamp used to compute staleness.</param>
        /// <returns>The classified stall state.</returns>
        public static ProviderStallKind Classify(
            DateTime? lastHeartbeatUtc,
            DateTime? lastProviderProgressUtc,
            double thresholdMinutes,
            DateTime nowUtc)
        {
            double effectiveThreshold = thresholdMinutes > 0 ? thresholdMinutes : 1.0;

            bool heartbeatStale = lastHeartbeatUtc.HasValue
                && (nowUtc - lastHeartbeatUtc.Value).TotalMinutes >= effectiveThreshold;

            bool providerStale = lastProviderProgressUtc.HasValue
                && (nowUtc - lastProviderProgressUtc.Value).TotalMinutes >= effectiveThreshold;

            // Provider-silent-stall requires a recorded provider-progress. A captain
            // whose provider has never reported progress is not yet classifiable as a
            // silent-provider stall; the orchestrator must wait until either the runtime
            // publishes its first step_finish (the provider-progress signal) or the
            // captain-wide heartbeat times out (which then classifies as a heartbeat stall).
            if (providerStale && lastHeartbeatUtc.HasValue && !heartbeatStale)
                return ProviderStallKind.ProviderSilentStall;

            if (heartbeatStale && providerStale)
                return ProviderStallKind.HeartbeatAndProviderStall;

            if (heartbeatStale)
                return ProviderStallKind.HeartbeatStall;

            return ProviderStallKind.None;
        }

        /// <summary>
        /// Startup-grace check for stall nudging. The provider-progress tracker is keyed per
        /// captain, so a progress timestamp left over from a PRIOR mission can classify a
        /// freshly launched mission as a silent stall within seconds of launch; the probe run
        /// of 2026-08-10 nudged a healthy captain 12 seconds after launch. A mission that
        /// started less than the threshold ago is never nudged, whatever the tracker says.
        /// </summary>
        /// <param name="missionStartedUtc">Mission start time, if recorded.</param>
        /// <param name="nowUtc">Reference "now" timestamp.</param>
        /// <param name="thresholdMinutes">Stall threshold in minutes.</param>
        /// <returns>True when the mission is still inside its startup grace window.</returns>
        public static bool IsWithinStartupGrace(DateTime? missionStartedUtc, DateTime nowUtc, double thresholdMinutes)
        {
            if (!missionStartedUtc.HasValue) return false;

            double effectiveThreshold = thresholdMinutes > 0 ? thresholdMinutes : 1.0;
            return (nowUtc - missionStartedUtc.Value).TotalMinutes < effectiveThreshold;
        }
    }
}