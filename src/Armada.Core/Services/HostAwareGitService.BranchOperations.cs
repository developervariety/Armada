namespace Armada.Core.Services
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Harbor;

    /// <summary>Routes worktree and reference mutations.</summary>
    public sealed partial class HostAwareGitService
    {
        /// <inheritdoc />
        public Task<string> CreatePullRequestAsync(string worktreePath, string title, string body, CancellationToken token = default)
        {
            if (!IsRemote(worktreePath)) return _Local.CreatePullRequestAsync(worktreePath, title, body, token);
            HarborGitOperationRequest request = NewRequest(HarborGitOperationEnum.CreatePullRequest, worktreePath);
            request.Title = title;
            request.Body = body;
            return SendValueAsync(worktreePath, request, token, result => RequireText(result, "text"));
        }

        /// <inheritdoc />
        public Task<string> CreatePullRequestAsync(string worktreePath, string title, string body, string baseBranch, CancellationToken token = default)
        {
            if (!IsRemote(worktreePath)) return _Local.CreatePullRequestAsync(worktreePath, title, body, baseBranch, token);
            HarborGitOperationRequest request = NewRequest(HarborGitOperationEnum.CreatePullRequest, worktreePath);
            request.Title = title;
            request.Body = body;
            request.BaseBranch = baseBranch;
            return SendValueAsync(worktreePath, request, token, result => RequireText(result, "text"));
        }

        /// <inheritdoc />
        public Task<string> CreatePullRequestAsync(string worktreePath, string title, string body, string headBranch, string baseBranch, CancellationToken token = default)
        {
            if (!IsRemote(worktreePath)) return _Local.CreatePullRequestAsync(worktreePath, title, body, headBranch, baseBranch, token);
            HarborGitOperationRequest request = NewRequest(HarborGitOperationEnum.CreatePullRequest, worktreePath);
            request.Title = title;
            request.Body = body;
            request.BranchName = headBranch;
            request.BaseBranch = baseBranch;
            return SendValueAsync(worktreePath, request, token, result => RequireText(result, "text"));
        }

        /// <inheritdoc />
        public Task DeleteLocalBranchAsync(string repoPath, string branchName, CancellationToken token = default, [System.Runtime.CompilerServices.CallerMemberName] string caller = "")
        {
            if (!IsRemote(repoPath)) return _Local.DeleteLocalBranchAsync(repoPath, branchName, token, caller);
            HarborGitOperationRequest request = NewRequest(HarborGitOperationEnum.DeleteLocalBranch, repoPath);
            request.BranchName = branchName;
            request.Reason = "DeleteLocalBranch";
            return SendVoidAsync(repoPath, request, token);
        }

        /// <inheritdoc />
        public Task DeleteRemoteBranchAsync(string repoPath, string branchName, CancellationToken token = default, [System.Runtime.CompilerServices.CallerMemberName] string caller = "")
        {
            if (!IsRemote(repoPath)) return _Local.DeleteRemoteBranchAsync(repoPath, branchName, token, caller);
            HarborGitOperationRequest request = NewRequest(HarborGitOperationEnum.DeleteRemoteBranch, repoPath);
            request.BranchName = branchName;
            request.Reason = "DeleteRemoteBranch";
            return SendVoidAsync(repoPath, request, token);
        }

        /// <inheritdoc />
        public Task PushRefSpecAsync(string repoPath, string srcRef, string destRef, CancellationToken token = default)
        {
            if (!IsRemote(repoPath)) return _Local.PushRefSpecAsync(repoPath, srcRef, destRef, token);
            HarborGitOperationRequest request = NewRequest(HarborGitOperationEnum.PushRefSpec, repoPath);
            request.SourceRef = srcRef;
            request.DestinationRef = destRef;
            return SendVoidAsync(repoPath, request, token);
        }

        /// <inheritdoc />
        public Task ForceUpdateBranchRefAsync(string repoPath, string branchName, string commitSha, CancellationToken token = default)
        {
            if (!IsRemote(repoPath)) return _Local.ForceUpdateBranchRefAsync(repoPath, branchName, commitSha, token);
            HarborGitOperationRequest request = NewRequest(HarborGitOperationEnum.ForceUpdateBranchRef, repoPath);
            request.BranchName = branchName;
            request.CommitSha = commitSha;
            return SendVoidAsync(repoPath, request, token);
        }

        /// <inheritdoc />
        public Task CompareAndSwapBranchRefAsync(string repoPath, string branchName, string newSha, string expectedOldSha, CancellationToken token = default)
        {
            if (!IsRemote(repoPath)) return _Local.CompareAndSwapBranchRefAsync(repoPath, branchName, newSha, expectedOldSha, token);
            HarborGitOperationRequest request = NewRequest(HarborGitOperationEnum.CompareAndSwapBranchRef, repoPath);
            request.BranchName = branchName;
            request.CommitSha = newSha;
            request.ExpectedOldSha = expectedOldSha;
            return SendVoidAsync(repoPath, request, token);
        }

        /// <inheritdoc />
        public Task PushHeadToBranchAsync(string worktreePath, string remoteName, string branchName, CancellationToken token = default)
        {
            if (!IsRemote(worktreePath)) return _Local.PushHeadToBranchAsync(worktreePath, remoteName, branchName, token);
            HarborGitOperationRequest request = NewRequest(HarborGitOperationEnum.PushHeadToBranch, worktreePath);
            request.RemoteName = remoteName;
            request.BranchName = branchName;
            return SendVoidAsync(worktreePath, request, token);
        }

    }
}
