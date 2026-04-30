namespace Armada.Core.Enums
{
    using System.Text.Json.Serialization;

    /// <summary>
    /// Classification of why a merge-queue entry failed. Drives the auto-recovery
    /// router's decision among redispatch, rebase-captain, and surface actions.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum MergeFailureClassEnum
    {
        /// <summary>
        /// Captain branch's merge-base is too old; main moved forward with non-overlapping
        /// commits. Often resolvable by redispatching the mission off a fresh target tip.
        /// </summary>
        StaleBase,

        /// <summary>
        /// Both branches edited the same file region. Whether redispatch or rebase-captain
        /// is appropriate depends on the conflict's triviality.
        /// </summary>
        TextConflict,

        /// <summary>
        /// Auto-fold succeeded but tests failed after the merge. Captain produced
        /// behaviorally-stale work; rebase-captain re-runs against the new tip.
        /// </summary>
        TestFailureAfterMerge,

        /// <summary>
        /// Captain produced broken work; tests failed before the merge attempt completed.
        /// Surfaced through Judge / NEEDS_REVISION rather than auto-recovery.
        /// </summary>
        TestFailureBeforeMerge,

        /// <summary>
        /// Failure shape could not be classified. The router must surface conservatively
        /// rather than guess at a recovery action.
        /// </summary>
        Unknown
    }
}
