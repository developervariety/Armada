namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Harbor;
    using Armada.Core.Models;

    /// <summary>Routes common checkout and repository reads.</summary>
    public sealed partial class HostAwareGitService
    {
        /// <inheritdoc />
        public Task<bool> IsPrMergedAsync(string workingDirectory, string prUrl, CancellationToken token = default)
        {
            if (!IsRemote(workingDirectory)) return _Local.IsPrMergedAsync(workingDirectory, prUrl, token);
            HarborGitOperationRequest request = NewRequest(HarborGitOperationEnum.IsPrMerged, workingDirectory);
            request.PullRequestUrl = prUrl;
            return SendValueAsync(workingDirectory, request, token, result => result.Flag ?? throw MissingResult("flag"));
        }

        /// <inheritdoc />
        public Task<bool> IsPathTrackedAsync(string worktreePath, string relativePath, CancellationToken token = default)
        {
            if (!IsRemote(worktreePath)) return _Local.IsPathTrackedAsync(worktreePath, relativePath, token);
            HarborGitOperationRequest request = NewRequest(HarborGitOperationEnum.IsPathTracked, worktreePath);
            request.RelativePath = relativePath;
            return SendValueAsync(worktreePath, request, token, result => result.Flag ?? throw MissingResult("flag"));
        }

        /// <inheritdoc />
        public Task<bool> BranchExistsAsync(string repoPath, string branchName, CancellationToken token = default)
        {
            if (!IsRemote(repoPath)) return _Local.BranchExistsAsync(repoPath, branchName, token);
            HarborGitOperationRequest request = NewRequest(HarborGitOperationEnum.BranchExists, repoPath);
            request.BranchName = branchName;
            return SendValueAsync(repoPath, request, token, result => result.Flag ?? throw MissingResult("flag"));
        }

        /// <inheritdoc />
        public Task<bool> EnsureLocalBranchAsync(string repoPath, string branchName, CancellationToken token = default)
        {
            if (!IsRemote(repoPath)) return _Local.EnsureLocalBranchAsync(repoPath, branchName, token);
            HarborGitOperationRequest request = NewRequest(HarborGitOperationEnum.EnsureLocalBranch, repoPath);
            request.BranchName = branchName;
            return SendValueAsync(repoPath, request, token, result => result.Flag ?? throw MissingResult("flag"));
        }

        /// <inheritdoc />
        public Task<bool> IsWorktreeRegisteredAsync(string repoPath, string worktreePath, CancellationToken token = default)
        {
            bool remoteRepo = IsRemote(repoPath);
            bool remoteWorktree = IsRemote(worktreePath);
            if (!remoteRepo && !remoteWorktree) return _Local.IsWorktreeRegisteredAsync(repoPath, worktreePath, token);
            if (!remoteRepo || !remoteWorktree) throw new InvalidOperationException("harbor_checkout_scope_mismatch");
            RequireRepositoryAddress(repoPath);
            SameRemoteScope(repoPath, worktreePath);
            HarborGitOperationRequest request = NewRequest(HarborGitOperationEnum.IsWorktreeRegistered, repoPath);
            SetTarget(request, worktreePath, repoPath);
            return SendValueAsync(repoPath, request, token, result => result.Flag ?? throw MissingResult("flag"));
        }
    }
}
