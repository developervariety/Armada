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
        public Task CloneBareAsync(string repoUrl, string localPath, CancellationToken token = default)
        {
            if (IsRemote(localPath)) throw new InvalidOperationException("harbor_checkout_clone_not_granted");
            if (IsRemote(repoUrl)) throw new InvalidOperationException("harbor_checkout_scope_mismatch");
            return _Local.CloneBareAsync(repoUrl, localPath, token);
        }

        /// <inheritdoc />
        public Task CreateWorktreeAsync(string repoPath, string worktreePath, string branchName, string baseBranch = "main", bool detached = false, CancellationToken token = default)
        {
            bool remoteRepo = IsRemote(repoPath);
            bool remoteTarget = IsRemote(worktreePath);
            if (remoteRepo || remoteTarget)
            {
                if (!remoteRepo || !remoteTarget) throw new InvalidOperationException("harbor_checkout_scope_mismatch");
                RequireRepositoryAddress(repoPath);
                SameRemoteScope(repoPath, worktreePath);
                HarborGitOperationRequest request = NewRequest(HarborGitOperationEnum.CreateWorktree, repoPath);
                request.BranchName = branchName;
                request.BaseBranch = baseBranch;
                request.Detached = detached;
                SetTarget(request, worktreePath, repoPath);
                return SendVoidAsync(repoPath, request, token);
            }
            return _Local.CreateWorktreeAsync(repoPath, worktreePath, branchName, baseBranch, detached, token);
        }

        /// <inheritdoc />
        public Task CreateDetachedWorktreeAtCommitAsync(string repoPath, string worktreePath, string commitSha, CancellationToken token = default)
        {
            bool remoteRepo = IsRemote(repoPath);
            bool remoteTarget = IsRemote(worktreePath);
            if (remoteRepo || remoteTarget)
            {
                if (!remoteRepo || !remoteTarget) throw new InvalidOperationException("harbor_checkout_scope_mismatch");
                RequireRepositoryAddress(repoPath);
                SameRemoteScope(repoPath, worktreePath);
                HarborGitOperationRequest request = NewRequest(HarborGitOperationEnum.CreateDetachedWorktreeAtCommit, repoPath);
                request.CommitSha = commitSha;
                SetTarget(request, worktreePath, repoPath);
                return SendVoidAsync(repoPath, request, token);
            }
            return _Local.CreateDetachedWorktreeAtCommitAsync(repoPath, worktreePath, commitSha, token);
        }

        /// <inheritdoc />
        public Task RemoveWorktreeAsync(string worktreePath, CancellationToken token = default)
        {
            if (!IsRemote(worktreePath)) return _Local.RemoveWorktreeAsync(worktreePath, token);
            HarborGitOperationRequest request = NewRequest(HarborGitOperationEnum.RemoveWorktree, worktreePath);
            return SendVoidAsync(worktreePath, request, token);
        }

        /// <inheritdoc />
        public Task LockWorktreeAsync(string worktreePath, string reason, CancellationToken token = default)
        {
            if (!IsRemote(worktreePath)) return _Local.LockWorktreeAsync(worktreePath, reason, token);
            HarborGitOperationRequest request = NewRequest(HarborGitOperationEnum.LockWorktree, worktreePath);
            request.Reason = reason;
            return SendVoidAsync(worktreePath, request, token);
        }

        /// <inheritdoc />
        public Task UnlockWorktreeAsync(string worktreePath, CancellationToken token = default)
        {
            if (!IsRemote(worktreePath)) return _Local.UnlockWorktreeAsync(worktreePath, token);
            return SendVoidAsync(worktreePath, NewRequest(HarborGitOperationEnum.UnlockWorktree, worktreePath), token);
        }

        /// <inheritdoc />
        public Task RepairWorktreeAsync(string worktreePath, CancellationToken token = default)
        {
            if (!IsRemote(worktreePath)) return _Local.RepairWorktreeAsync(worktreePath, token);
            return SendVoidAsync(worktreePath, NewRequest(HarborGitOperationEnum.RepairWorktree, worktreePath), token);
        }

        /// <inheritdoc />
        public Task RecreateMissingWorktreeAsync(string repoPath, string worktreePath, string branchName, CancellationToken token = default)
        {
            if (IsRemote(repoPath) || IsRemote(worktreePath)) throw new InvalidOperationException("harbor_checkout_reconstruction_not_granted");
            return _Local.RecreateMissingWorktreeAsync(repoPath, worktreePath, branchName, token);
        }

        /// <inheritdoc />
        public Task FetchAsync(string repoPath, CancellationToken token = default)
        {
            if (!IsRemote(repoPath)) return _Local.FetchAsync(repoPath, token);
            return SendVoidAsync(repoPath, NewRequest(HarborGitOperationEnum.Fetch, repoPath), token);
        }

        /// <inheritdoc />
        public Task PushBranchAsync(string worktreePath, string remoteName = "origin", CancellationToken token = default)
        {
            if (!IsRemote(worktreePath)) return _Local.PushBranchAsync(worktreePath, remoteName, token);
            HarborGitOperationRequest request = NewRequest(HarborGitOperationEnum.PushBranch, worktreePath);
            request.RemoteName = remoteName;
            return SendVoidAsync(worktreePath, request, token);
        }

    }
}
