namespace Armada.Runtimes.Harbor
{
    using System;
    using Armada.Core.Harbor;

    public sealed partial class HarborRunnerWorkspace
    {
        private static bool AllowsRetainedRepositoryOperation(HarborWorkspaceOperationRequest request)
        {
            HarborGitOperationRequest? git = request.Git;
            if (git == null || !git.UseRepository || git.UseWorkingDirectory || request.Binding == null) return false;
            if (!HarborWorkspaceOperationRules.IsGitMutation(git.Operation)) return true;
            return (git.Operation == HarborGitOperationEnum.DeleteRefIfAt
                    || git.Operation == HarborGitOperationEnum.DeleteRemoteRefIfAt)
                && IsFullCommit(git.ExpectedOldSha)
                && String.Equals(git.DestinationRef, "refs/heads/" + request.Binding.BranchName, StringComparison.Ordinal);
        }
    }
}
