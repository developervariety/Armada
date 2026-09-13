namespace Armada.Core.Models
{
    using Armada.Core.Enums;

    /// <summary>
    /// Read-only auto-land detail for one mission: the vessel's current predicate, the latest recorded predicate
    /// decision, and the latest merge entry with its audit fields. Reading it evaluates no predicate and reads no diff.
    /// </summary>
    public class MissionAutoLandReport
    {
        #region Public-Members

        /// <summary>
        /// Mission identifier.
        /// </summary>
        public string MissionId { get; set; } = "";

        /// <summary>
        /// Whether the vessel has an auto-land predicate configured.
        /// </summary>
        public bool PredicateConfigured { get; set; } = false;

        /// <summary>
        /// Current parsed predicate, or null when none is configured, it cannot be parsed, or the vessel is unreadable.
        /// </summary>
        public AutoLandPredicate? CurrentPredicate { get; set; } = null;

        /// <summary>
        /// Reason the current predicate is not shown, or null.
        /// </summary>
        public string? PredicateUnavailableReason { get; set; } = null;

        /// <summary>
        /// Whether the latest decision can be reported.
        /// </summary>
        public RecordedHistoryStateEnum DecisionState { get; set; } = RecordedHistoryStateEnum.NotRecorded;

        /// <summary>
        /// Reason the latest decision is unavailable, or null.
        /// </summary>
        public string? DecisionUnavailableReason { get; set; } = null;

        /// <summary>
        /// Latest recorded decision when <see cref="DecisionState"/> is Recorded; otherwise null.
        /// </summary>
        public MissionAutoLandDecision? LatestDecision { get; set; } = null;

        /// <summary>
        /// Latest merge entry for the mission in the caller's scope, or null.
        /// </summary>
        public MissionAutoLandMergeEntry? LatestMergeEntry { get; set; } = null;

        /// <summary>
        /// Reason the merge entry could not be read, or null.
        /// </summary>
        public string? MergeEntryUnavailableReason { get; set; } = null;

        #endregion
    }
}
