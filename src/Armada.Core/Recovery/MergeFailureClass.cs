namespace Armada.Core.Recovery
{
    /// <summary>
    /// Classification of a merge-queue failure shape produced by the auto-fold
    /// or post-merge test step. Persisted on <c>merge_entries.merge_failure_class</c>
    /// so the recovery router can decide between Redispatch / RebaseCaptain / Surface.
    /// Null on the database row means "not yet classified" (entry has not failed,
    /// or pre-recovery row).
    /// </summary>
    public enum MergeFailureClass
    {
        /// <summary>
        /// Captain branch was based on a stale target ref; main moved forward
        /// with non-overlapping commits. A redispatch off the fresh target tip
        /// is expected to resolve cleanly without any captain intervention.
        /// </summary>
        StaleBase = 0,

        /// <summary>
        /// Two branches edited the same file region; the 3-way fold left
        /// conflict markers in one or more files. ConflictedFiles is populated.
        /// </summary>
        TextConflict = 1,

        /// <summary>
        /// 3-way fold succeeded but the post-merge test command failed --
        /// main introduced a behavioral change that the captain wasn't aware
        /// of. Recovery requires the captain to rebase and adjust.
        /// </summary>
        TestFailureAfterMerge = 2,

        /// <summary>
        /// Captain produced broken work; tests failed before any merge attempt
        /// (or in a way that's clearly the captain's fault, not a collision
        /// with main). Out of auto-recovery scope -- routed to surface so the
        /// Judge / NEEDS_REVISION path handles it.
        /// </summary>
        TestFailureBeforeMerge = 3,

        /// <summary>
        /// Failure shape could not be parsed from the available signals.
        /// Conservative routing: surface, never guess.
        /// </summary>
        Unknown = 4
    }
}
