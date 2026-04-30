namespace Armada.Core.Recovery
{
    /// <summary>
    /// Terminal action selected by the recovery router for a failed merge-queue
    /// entry. Each action ends the auto-recovery loop for the current attempt;
    /// recursion is bounded by <see cref="Armada.Core.Models.Mission.RecoveryAttempts"/>.
    /// </summary>
    public enum RecoveryAction
    {
        /// <summary>
        /// Re-run the original mission against a fresh target tip via
        /// <c>armada_restart_mission</c>. The mission ID is preserved so any
        /// dependents (cross-vessel or alias-chained) keep resolving against
        /// the same identifier.
        /// </summary>
        Redispatch = 0,

        /// <summary>
        /// Spawn a new high-tier mission whose dock is pre-staged with the
        /// captain branch in conflict state plus the original brief and a
        /// conflict-context appendix. Resolution commits land back on the
        /// original captain branch.
        /// </summary>
        RebaseCaptain = 1,

        /// <summary>
        /// Stop the auto-recovery loop for this entry and route it to the
        /// existing PR-fallback channel (via
        /// <c>merge_entries.audit_critical_trigger = "recovery_exhausted"</c>)
        /// for human resolution.
        /// </summary>
        Surface = 2
    }
}
