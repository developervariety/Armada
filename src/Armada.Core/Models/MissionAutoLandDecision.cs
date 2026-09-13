namespace Armada.Core.Models
{
    using System;
    using Armada.Core.Enums;

    /// <summary>
    /// Latest recorded auto-land decision for a mission, read from its scoped auto-land event.
    /// </summary>
    public class MissionAutoLandDecision
    {
        #region Public-Members

        /// <summary>
        /// Decision outcome.
        /// </summary>
        public AutoLandDecisionOutcomeEnum Outcome { get; set; } = AutoLandDecisionOutcomeEnum.Skipped;

        /// <summary>
        /// Event identifier.
        /// </summary>
        public string EventId { get; set; } = "";

        /// <summary>
        /// Merge entry the decision applied to.
        /// </summary>
        public string MergeEntryId { get; set; } = "";

        /// <summary>
        /// Redacted and bounded skip reason, or null for a triggered decision.
        /// </summary>
        public string? Reason { get; set; } = null;

        /// <summary>
        /// Predicate recorded at decision time, or null when the event did not carry one.
        /// </summary>
        public AutoLandPredicate? PredicateAtDecision { get; set; } = null;

        /// <summary>
        /// UTC time the decision was recorded.
        /// </summary>
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

        #endregion
    }
}
