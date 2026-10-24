namespace Armada.Core.Harbor
{
    using System;

    /// <summary>One conservative classification for checkout mutation admission and replay protection.</summary>
    public static class HarborWorkspaceOperationRules
    {
        /// <summary>Whether an operation can change files, refs, verification outputs or checkout ownership.</summary>
        /// <param name="request">Typed operation.</param>
        /// <returns>True for mutations and unknown operations.</returns>
        public static bool IsMutation(HarborWorkspaceOperationRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (request.Git != null && request.Operation != HarborWorkspaceOperationEnum.Git) return true;
            if (request.Verification != null && request.Operation != HarborWorkspaceOperationEnum.RunVerification) return true;
            if (request.VerificationOutput != null && request.Operation != HarborWorkspaceOperationEnum.ReadVerificationOutput) return true;
            return request.Operation switch
            {
                HarborWorkspaceOperationEnum.InspectRepository => false,
                HarborWorkspaceOperationEnum.InspectCheckout => false,
                HarborWorkspaceOperationEnum.Stat => false,
                HarborWorkspaceOperationEnum.ReadText => false,
                HarborWorkspaceOperationEnum.ReadBytes => false,
                HarborWorkspaceOperationEnum.ReadVerificationOutput => false,
                HarborWorkspaceOperationEnum.EnumerateFiles => false,
                HarborWorkspaceOperationEnum.Git => request.Git == null || IsGitMutation(request.Git.Operation),
                _ => true
            };
        }

        /// <summary>Whether a fixed Git operation can change repository or working-tree state.</summary>
        /// <param name="operation">Fixed Git service operation.</param>
        /// <returns>True unless the operation is a known read.</returns>
        public static bool IsGitMutation(HarborGitOperationEnum operation)
        {
            return operation switch
            {
                HarborGitOperationEnum.GetRepositoryHeadRef => false,
                HarborGitOperationEnum.InspectRepositoryHead => false,
                HarborGitOperationEnum.IsBareRepository => false,
                HarborGitOperationEnum.IsRepository => false,
                HarborGitOperationEnum.GetCurrentBranch => false,
                HarborGitOperationEnum.IsWorkingDirectoryClean => false,
                HarborGitOperationEnum.HasUncommittedTrackedChanges => false,
                HarborGitOperationEnum.Diff => false,
                HarborGitOperationEnum.GetHeadCommitHash => false,
                HarborGitOperationEnum.GetConflictedFiles => false,
                HarborGitOperationEnum.GetRevisionSha => false,
                HarborGitOperationEnum.GetRevisionCommitSha => false,
                HarborGitOperationEnum.GetCommitTimeUtc => false,
                HarborGitOperationEnum.ResolveAnchorPathOnRevision => false,
                HarborGitOperationEnum.MatchAnchorPathOnRevision => false,
                HarborGitOperationEnum.ReadFileLinesOnRevision => false,
                HarborGitOperationEnum.GetCommitsTouchingPathOnRevision => false,
                HarborGitOperationEnum.SearchTrackedContentOnRevision => false,
                HarborGitOperationEnum.ReadFileExcerptOnRevision => false,
                HarborGitOperationEnum.TryIsAncestor => false,
                HarborGitOperationEnum.GetCommitsTouchingPath => false,
                HarborGitOperationEnum.PathExistsOnRevision => false,
                HarborGitOperationEnum.ResolveTrackedPathSuffix => false,
                HarborGitOperationEnum.ListTrackedPathCandidates => false,
                HarborGitOperationEnum.SearchTrackedContent => false,
                HarborGitOperationEnum.GetChangedFilesSince => false,
                HarborGitOperationEnum.GetChangedFilePathsAgainstBase => false,
                HarborGitOperationEnum.ReadChangedPathsBetweenCommits => false,
                HarborGitOperationEnum.ReadChangedPathStatusesBetweenCommits => false,
                HarborGitOperationEnum.ReadChangedPathsAgainstBase => false,
                HarborGitOperationEnum.IsPrMerged => false,
                HarborGitOperationEnum.IsPathTracked => false,
                HarborGitOperationEnum.BranchExists => false,
                HarborGitOperationEnum.IsWorktreeRegistered => false,
                HarborGitOperationEnum.GetCommitCountBetween => false,
                HarborGitOperationEnum.TryCountCommitsBetween => false,
                HarborGitOperationEnum.ListBranches => false,
                HarborGitOperationEnum.EnumerateLocalBranches => false,
                HarborGitOperationEnum.IsAncestor => false,
                HarborGitOperationEnum.EnumerateRefTips => false,
                HarborGitOperationEnum.EnumerateRemoteRefTips => false,
                HarborGitOperationEnum.TryGetCommitTimeUtc => false,
                _ => true
            };
        }
    }
}
