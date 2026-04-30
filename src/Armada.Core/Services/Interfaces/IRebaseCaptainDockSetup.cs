namespace Armada.Core.Services.Interfaces
{
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;

    /// <summary>
    /// Builds the <see cref="RebaseCaptainMissionSpec"/> for a rebase-captain mission:
    /// the brief (original brief plus a conflict-context appendix), the prestaged-files
    /// list that puts the captain branch in conflict state inside the new mission's
    /// dock, the landing target branch (the failed mission's captain branch -- the
    /// resolution lands on the SAME captain branch), and the selected playbooks
    /// (pbk_rebase_captain in InlineFullContent mode).
    /// </summary>
    public interface IRebaseCaptainDockSetup
    {
        /// <summary>
        /// Build the rebase-captain mission spec for the supplied failed merge entry.
        /// </summary>
        /// <param name="failedEntry">The failed merge-queue entry.</param>
        /// <param name="failedMission">The mission that produced the failed entry.</param>
        /// <param name="classification">Fail-time classification of the failure.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The mission spec used to create the rebase-captain mission.</returns>
        Task<RebaseCaptainMissionSpec> BuildAsync(
            MergeEntry failedEntry,
            Mission failedMission,
            MergeFailureClassification classification,
            CancellationToken token = default);
    }

    /// <summary>
    /// Description of a rebase-captain mission to be created by the recovery handler.
    /// </summary>
    /// <param name="Brief">Mission description (original brief plus conflict appendix).</param>
    /// <param name="PrestagedFiles">Files to be staged into the new mission's dock,
    /// including the captain branch checked out at conflict state.</param>
    /// <param name="PreferredModel">Preferred captain model (high-tier rebase per spec).</param>
    /// <param name="LandingTargetBranch">Branch the resolution commits land on (the
    /// failed mission's captain branch).</param>
    /// <param name="SelectedPlaybooks">Playbooks delivered with the rebase mission.</param>
    /// <param name="DependsOnMissionId">Optional mission this rebase depends on. Null
    /// when the rebase mission can run immediately.</param>
    /// <param name="RecoveryAttempts">Initial recovery-attempt counter on the rebase
    /// mission. Always 0 -- the rebase mission has its own budget and does not
    /// inherit from the failed mission.</param>
    public sealed record RebaseCaptainMissionSpec(
        string Brief,
        IReadOnlyList<PrestagedFile> PrestagedFiles,
        string PreferredModel,
        string LandingTargetBranch,
        IReadOnlyList<SelectedPlaybook> SelectedPlaybooks,
        string? DependsOnMissionId,
        int RecoveryAttempts);
}
