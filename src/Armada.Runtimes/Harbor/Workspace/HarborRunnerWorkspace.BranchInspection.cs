namespace Armada.Runtimes.Harbor
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>Validates a pre-existing local branch before creating a bound checkout.</summary>
    public sealed partial class HarborRunnerWorkspace
    {
        private async Task<bool> HasMatchingExistingBranchAsync(HarborRepositoryGrant grant, Armada.Core.Models.HarborCheckoutBinding binding,
            bool detached, CancellationToken token)
        {
            if (detached) return false;
            HarborWorkspaceProcessResult branch = await RunGitAsync(grant.RepositoryPath,
                new[] { "check-ref-format", "--branch", binding.BranchName }, TimeSpan.FromSeconds(10), 4096, token).ConfigureAwait(false);
            RequireSuccess(branch, "workspace_branch_invalid");
            HarborWorkspaceProcessResult existing = await RunGitAsync(grant.RepositoryPath,
                new[] { "show-ref", "--verify", "--quiet", "refs/heads/" + binding.BranchName }, TimeSpan.FromSeconds(10), 4096, token).ConfigureAwait(false);
            if (existing.ExitCode == 1) return false;
            if (existing.ExitCode != 0)
                throw Failure("workspace_branch_inspection_failed", "The local branch state could not be verified.");

            HarborWorkspaceProcessResult branchHead = await RunGitAsync(grant.RepositoryPath,
                new[] { "rev-parse", "--verify", "refs/heads/" + binding.BranchName + "^{commit}" }, TimeSpan.FromSeconds(10), 4096, token).ConfigureAwait(false);
            RequireSuccess(branchHead, "workspace_branch_invalid");
            if (!String.Equals(branchHead.StandardOutput.Trim(), binding.BaseCommit, StringComparison.OrdinalIgnoreCase))
                throw Failure("workspace_branch_base_conflict", "The existing local branch does not match the pinned checkout base.");
            return true;
        }
    }
}
