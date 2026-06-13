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
        /// Minutes between scheduler sweep ticks.
        /// </summary>
        public int IntervalMinutes { get; set; } = 25;

        /// <summary>
        /// Maximum number of objectives that may have simultaneously active linked voyages.
        /// </summary>
        public int MaxConcurrentVoyages { get; set; } = 1;

        /// <summary>
        /// UTC timestamp of the last completed sweep tick, or null if no tick has run yet.
        /// </summary>
        public DateTime? LastTickUtc { get; set; } = null;

        /// <summary>
        /// Count of objectives with an active linked voyage as of the last sweep tick.
        /// </summary>
        public int ActiveDispatchedCount { get; set; } = 0;

        /// <summary>
        /// Human-readable reason the last sweep was skipped (e.g. "disabled", "paused", "max_concurrent"),
        /// or null when the last sweep ran to completion.
        /// </summary>
        public string? LastSkipReason { get; set; } = null;

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
