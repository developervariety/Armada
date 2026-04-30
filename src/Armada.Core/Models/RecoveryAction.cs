namespace Armada.Core.Models
{
    /// <summary>
    /// Terminal action selected by <c>IRecoveryRouter</c> for a failed merge-queue entry.
    /// One of: redispatch the original mission, create a rebase-captain mission, or
    /// surface to the PR-fallback channel for human resolution.
    /// </summary>
    public abstract record RecoveryAction
    {
        /// <summary>
        /// Redispatch the original mission via <c>armada_restart_mission</c> so its ID
        /// (and any cross-mission dependents) is preserved.
        /// </summary>
        public sealed record Redispatch : RecoveryAction;

        /// <summary>
        /// Create a new high-tier rebase-captain mission whose dock is pre-staged with
        /// the captain branch in conflict state plus the original brief and a
        /// conflict-context appendix.
        /// </summary>
        public sealed record RebaseCaptain : RecoveryAction;

        /// <summary>
        /// Surface the entry to the PR-fallback channel by setting its critical-trigger
        /// reason. Recovery loop terminates without further automation.
        /// </summary>
        /// <param name="Reason">Surface reason recorded on the merge entry.</param>
        public sealed record Surface(string Reason) : RecoveryAction;
    }
}
