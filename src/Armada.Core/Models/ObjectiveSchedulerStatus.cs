namespace Armada.Core.Models
{
    using System;

    /// <summary>
    /// Snapshot of the autonomous objective scheduler's runtime state.
    /// </summary>
    public class ObjectiveSchedulerStatus
    {
        #region Public-Members

        /// <summary>
        /// Whether the scheduler is allowed to auto-dispatch eligible objectives.
        /// </summary>
        public bool Enabled { get; set; } = false;

        /// <summary>
        /// Whether the scheduler is temporarily paused without clearing the Enabled flag.
        /// </summary>
        public bool Paused { get; set; } = false;

        /// <summary>
        /// Participant key of the session that set the pause, or null when unattributed.
        /// </summary>
        public string? PausedBy { get; set; } = null;

        /// <summary>
        /// UTC time the pause was set, or null when unattributed.
        /// </summary>
        public DateTime? PausedUtc { get; set; } = null;

        /// <summary>
        /// Why the pause was set, or null.
        /// </summary>
        public string? PauseReason { get; set; } = null;

        /// <summary>
        /// True when the current state was written to the settings file and will survive a restart.
        /// Null when the status was read rather than set.
        /// </summary>
        public bool? SettingsPersisted { get; set; } = null;

        /// <summary>
        /// Minutes between scheduler sweep ticks.
        /// </summary>
        public int IntervalMinutes { get; set; } = 25;

        /// <summary>
        /// Maximum number of objectives that may have simultaneously active linked voyages.
        /// </summary>
        public int MaxConcurrentVoyages { get; set; } = 1;

        /// <summary>
        /// Maximum active objective voyages allowed on one vessel.
        /// </summary>
        public int MaxConcurrentVoyagesPerVessel { get; set; } = 1;

        /// <summary>
        /// Whether campaign fair-share is active within each priority band.
        /// </summary>
        public bool FairShareWithinPriorityBands { get; set; } = false;

        /// <summary>
        /// Last successfully served campaign key in each priority band for this process.
        /// </summary>
        public Dictionary<string, string> LastServedCampaignByPriority { get; set; } = new Dictionary<string, string>();

        /// <summary>
        /// UTC timestamp of the last completed sweep tick, or null if no tick has run yet.
        /// </summary>
        public DateTime? LastTickUtc { get; set; } = null;

        /// <summary>
        /// Number of objectives that have an active linked voyage, as of the last sweep tick.
        /// Counts operator-dispatched voyages too, so it can exceed MaxConcurrentVoyages: the
        /// limit gates what the scheduler starts, not what an operator starts.
        /// </summary>
        public int ActiveDispatchedCount { get; set; } = 0;

        /// <summary>
        /// Number of debounced event-triggered sweeps that started in this process.
        /// </summary>
        public long EventTriggeredSweepCount { get; set; } = 0;

        /// <summary>
        /// Human-readable reason the last sweep was skipped (e.g. "disabled", "paused", "max_concurrent"),
        /// or null when the last sweep ran to completion.
        /// </summary>
        public string? LastSkipReason { get; set; } = null;

        /// <summary>
        /// True while a scheduler sweep is running.
        /// </summary>
        public bool SweepInProgress { get; set; } = false;

        /// <summary>
        /// UTC timestamp at which the current or most recent sweep started.
        /// </summary>
        public DateTime? LastSweepStartedUtc { get; set; } = null;

        /// <summary>
        /// UTC timestamp at which the most recent sweep completed or failed.
        /// </summary>
        public DateTime? LastSweepCompletedUtc { get; set; } = null;

        /// <summary>
        /// Total candidate count in the current or most recent sweep snapshot.
        /// </summary>
        public int SweepCandidateCount { get; set; } = 0;

        /// <summary>
        /// Candidates examined in the current or most recent sweep.
        /// </summary>
        public int SweepCandidatesExamined { get; set; } = 0;

        /// <summary>
        /// Voyages dispatched in the current or most recent sweep.
        /// </summary>
        public int SweepDispatchedCount { get; set; } = 0;

        /// <summary>
        /// True when candidate or elapsed-time limits stopped the most recent sweep.
        /// </summary>
        public bool LastSweepBoundReached { get; set; } = false;

        /// <summary>
        /// Most recent sweep-level or candidate-dispatch error, or null.
        /// </summary>
        public string? LastSweepError { get; set; } = null;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate with default values.
        /// </summary>
        public ObjectiveSchedulerStatus()
        {
        }

        #endregion
    }
}
