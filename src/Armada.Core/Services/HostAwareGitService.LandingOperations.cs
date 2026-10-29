namespace Armada.Core.Services
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Harbor;
    using Armada.Core.Services.Interfaces;

    /// <summary>Routes worktree and reference mutations.</summary>
    public sealed partial class HostAwareGitService
    {
        /// <inheritdoc />
        public Task CopyRefAsync(string repoPath, string srcRef, string destRef, CancellationToken token = default)
        {
            if (!IsRemote(repoPath)) return _Local.CopyRefAsync(repoPath, srcRef, destRef, token);
            HarborGitOperationRequest request = NewRequest(HarborGitOperationEnum.CopyRef, repoPath);
            request.SourceRef = srcRef;
            request.DestinationRef = destRef;
            return SendVoidAsync(repoPath, request, token);
        }

        /// <inheritdoc />
        public Task SetRepositoryHeadAsync(string repoPath, string branchName, CancellationToken token = default)
        {
            if (!IsRemote(repoPath)) return _Local.SetRepositoryHeadAsync(repoPath, branchName, token);
            HarborGitOperationRequest request = NewRequest(HarborGitOperationEnum.SetRepositoryHead, repoPath);
            request.BranchName = branchName;
            return SendVoidAsync(repoPath, request, token);
        }

        /// <inheritdoc />
        public Task PruneWorktreesAsync(string repoPath, CancellationToken token = default)
        {
            if (!IsRemote(repoPath)) return _Local.PruneWorktreesAsync(repoPath, token);
            return SendVoidAsync(repoPath, NewRequest(HarborGitOperationEnum.PruneWorktrees, repoPath), token);
        }

        /// <inheritdoc />
        public Task EnableAutoMergeAsync(string worktreePath, string prUrl, CancellationToken token = default)
        {
            if (!IsRemote(worktreePath)) return _Local.EnableAutoMergeAsync(worktreePath, prUrl, token);
            HarborGitOperationRequest request = NewRequest(HarborGitOperationEnum.EnableAutoMerge, worktreePath);
            request.PullRequestUrl = prUrl;
            return SendVoidAsync(worktreePath, request, token);
        }

        /// <inheritdoc />
        public Task MergeBranchLocalAsync(string targetWorkDir, string sourceRepoPath, string branchName, string? targetBranch = null, string? commitMessage = null, CancellationToken token = default)
        {
            bool remoteTarget = IsRemote(targetWorkDir);
            bool remoteSource = IsRemote(sourceRepoPath);
            if (!remoteTarget && !remoteSource) return _Local.MergeBranchLocalAsync(targetWorkDir, sourceRepoPath, branchName, targetBranch, commitMessage, token);
            if (!remoteTarget || !remoteSource) throw new InvalidOperationException("harbor_checkout_scope_mismatch");
            SameRemoteScope(targetWorkDir, sourceRepoPath);
            if (!HarborCheckoutAddress.TryParse(sourceRepoPath, out HarborCheckoutAddress? source) || source == null || !source.UseRepository)
                throw new InvalidOperationException("harbor_repository_address_required");
            if (!HarborCheckoutAddress.TryParse(targetWorkDir, out HarborCheckoutAddress? target) || target == null || target.UseRepository)
                throw new InvalidOperationException("harbor_checkout_address_required");
            HarborGitOperationRequest request = NewRequest(HarborGitOperationEnum.MergeBranchLocal, targetWorkDir);
            request.BranchName = branchName;
            request.TargetBranch = targetBranch;
            request.CommitMessage = commitMessage;
            return SendVoidAsync(targetWorkDir, request, token);
        }

        /// <inheritdoc />
        public Task PushHeadToRepositoryBranchAsync(string worktreePath, string repositoryPath, string branchName, CancellationToken token = default)
        {
            bool remoteCheckout = IsRemote(worktreePath);
            bool remoteRepository = IsRemote(repositoryPath);
            if (!remoteCheckout && !remoteRepository) return _Local.PushHeadToRepositoryBranchAsync(worktreePath, repositoryPath, branchName, token);
            if (!remoteCheckout || !remoteRepository) throw new InvalidOperationException("harbor_checkout_scope_mismatch");
            SameRemoteScope(worktreePath, repositoryPath);
            if (!HarborCheckoutAddress.TryParse(repositoryPath, out HarborCheckoutAddress? repository) || repository == null || !repository.UseRepository)
                throw new InvalidOperationException("harbor_repository_address_required");
            if (!HarborCheckoutAddress.TryParse(worktreePath, out HarborCheckoutAddress? checkout) || checkout == null || checkout.UseRepository)
                throw new InvalidOperationException("harbor_checkout_address_required");
            HarborGitOperationRequest request = NewRequest(HarborGitOperationEnum.PushHeadToRepositoryBranch, worktreePath);
            request.BranchName = branchName;
            return SendVoidAsync(worktreePath, request, token);
        }

        /// <inheritdoc />
        public Task<Armada.Core.Models.DockWorkSnapshot> SnapshotDockWorkAsync(string worktreePath, string missionId, string message, CancellationToken token = default)
        {
            if (!IsRemote(worktreePath)) return _Local.SnapshotDockWorkAsync(worktreePath, missionId, message, token);
            HarborGitOperationRequest request = NewRequest(HarborGitOperationEnum.SnapshotDockWork, worktreePath);
            request.MissionId = missionId;
            request.CommitMessage = message;
            return SendValueAsync(worktreePath, request, token, result => result.Snapshot ?? throw MissingResult("snapshot"));
        }

        /// <inheritdoc />
        public Task<Armada.Core.Models.DockWorkRestore> ApplyDockWorkAsync(string worktreePath, string missionId, CancellationToken token = default)
        {
            if (IsRemote(worktreePath) || _Local is not IDockWorkSnapshotApplier applier)
                return Task.FromResult(Armada.Core.Models.DockWorkRestore.NothingSaved());
            return applier.ApplyDockWorkAsync(worktreePath, missionId, token);
        }
    }
}
