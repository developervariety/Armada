namespace Armada.Core.Services
{
    using Armada.Core.Enums;
    using Armada.Core.Services.Interfaces;

    /// <summary>Integrate the latest known remote and local target before landing a mission branch.</summary>
    public static class LandingTargetSynchronization
    {
        /// <summary>Fetch current target refs and merge them into the mission dock. Return whether its head changed.</summary>
        public static async Task<bool> SynchronizeAsync(IGitService git, string worktree, string targetBranch, string? targetCheckout, CancellationToken token = default)
        {
            string? before = await git.GetHeadCommitHashAsync(worktree, token).ConfigureAwait(false);
            await git.FetchRemotesAsync(worktree, token).ConfigureAwait(false);
            string? remoteTip = await git.ResolveBranchTipAsync(worktree, "origin/" + targetBranch, token).ConfigureAwait(false);
            if (!String.IsNullOrEmpty(remoteTip) && remoteTip != before)
            {
                var merged = await git.MergeCommitAsync(worktree, remoteTip, targetBranch, "Integrate current landing target " + targetBranch, token).ConfigureAwait(false);
                if (merged.Outcome != GitCommitMergeOutcomeEnum.Merged && merged.Outcome != GitCommitMergeOutcomeEnum.AlreadyContained)
                    throw new InvalidOperationException("Cannot integrate current landing target: " + merged.Message + " " + String.Join(", ", merged.ConflictedFiles));
            }
            string? localTip = String.IsNullOrEmpty(targetCheckout) ? null : await git.ResolveBranchTipAsync(targetCheckout, targetBranch, token).ConfigureAwait(false);
            if (!String.IsNullOrEmpty(localTip) && localTip != before && localTip != remoteTip)
                await git.MergeBranchLocalAsync(worktree, targetCheckout!, targetBranch, token: token).ConfigureAwait(false);
            else if (String.IsNullOrEmpty(remoteTip) && String.IsNullOrEmpty(localTip))
                throw new InvalidOperationException("Current landing target could not be resolved; refresh the target refs and retry landing.");
            string? after = await git.GetHeadCommitHashAsync(worktree, token).ConfigureAwait(false);
            return !String.Equals(before, after, StringComparison.Ordinal);
        }
    }
}
