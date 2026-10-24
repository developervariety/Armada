namespace Armada.Runtimes.Harbor.Workspace.Git
{
    using Armada.Core.Harbor;

    public static partial class HarborGitDispatcher
    {
        private static void ValidateRepositoryChoice(HarborGitOperationRequest request)
        {
            if (!request.UseRepository) return;
            bool allowed = request.Operation is HarborGitOperationEnum.Fetch
                or HarborGitOperationEnum.PushBranch
                or HarborGitOperationEnum.IsRepository
                or HarborGitOperationEnum.DeleteLocalBranch
                or HarborGitOperationEnum.DeleteRemoteBranch
                or HarborGitOperationEnum.PushRefSpec
                or HarborGitOperationEnum.ForceUpdateBranchRef
                or HarborGitOperationEnum.CompareAndSwapBranchRef
                or HarborGitOperationEnum.CopyRef
                or HarborGitOperationEnum.GetRepositoryHeadRef
                or HarborGitOperationEnum.SetRepositoryHead
                or HarborGitOperationEnum.PruneWorktrees
                or HarborGitOperationEnum.GetRevisionSha
                or HarborGitOperationEnum.GetRevisionCommitSha
                or HarborGitOperationEnum.GetHeadCommitHash
                or HarborGitOperationEnum.IsPrMerged
                or HarborGitOperationEnum.GetCommitTimeUtc
                or HarborGitOperationEnum.TryIsAncestor
                or HarborGitOperationEnum.ReadChangedPathsBetweenCommits
                or HarborGitOperationEnum.ReadChangedPathStatusesBetweenCommits
                or HarborGitOperationEnum.BranchExists
                or HarborGitOperationEnum.EnsureLocalBranch
                or HarborGitOperationEnum.IsWorktreeRegistered
                or HarborGitOperationEnum.SetHeadSymbolicRef
                or HarborGitOperationEnum.GetCommitCountBetween
                or HarborGitOperationEnum.TryCountCommitsBetween
                or HarborGitOperationEnum.EnumerateLocalBranches
                or HarborGitOperationEnum.ListBranches
                or HarborGitOperationEnum.InspectRepositoryHead
                or HarborGitOperationEnum.IsBareRepository
                or HarborGitOperationEnum.IsAncestor
                or HarborGitOperationEnum.EnumerateRefTips
                or HarborGitOperationEnum.EnumerateRemoteRefTips
                or HarborGitOperationEnum.TryGetCommitTimeUtc
                or HarborGitOperationEnum.DeleteRefIfAt
                or HarborGitOperationEnum.DeleteRemoteRefIfAt;
            if (!allowed) throw Invalid("repository_path_not_allowed_for_operation");
        }
    }
}
