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
        public Task SetHeadSymbolicRefAsync(string repoPath, string targetRef, CancellationToken token = default)
        {
            if (!IsRemote(repoPath)) return _Local.SetHeadSymbolicRefAsync(repoPath, targetRef, token);
            HarborGitOperationRequest request = NewRequest(HarborGitOperationEnum.SetHeadSymbolicRef, repoPath);
            request.TargetRef = targetRef;
            return SendVoidAsync(repoPath, request, token);
        }

        /// <inheritdoc />
        public Task<int> GetCommitCountBetweenAsync(string repoPath, string fromRef, string toRef, CancellationToken token = default)
        {
            if (!IsRemote(repoPath)) return _Local.GetCommitCountBetweenAsync(repoPath, fromRef, toRef, token);
            HarborGitOperationRequest request = NewRequest(HarborGitOperationEnum.GetCommitCountBetween, repoPath);
            request.FromCommit = fromRef;
            request.ToCommit = toRef;
            return SendValueAsync(repoPath, request, token, result => result.Number ?? throw MissingResult("number"));
        }

        /// <inheritdoc />
        public Task<int?> TryCountCommitsBetweenAsync(string repoPath, string fromRef, string toRef, CancellationToken token = default)
        {
            if (!IsRemote(repoPath)) return _Local.TryCountCommitsBetweenAsync(repoPath, fromRef, toRef, token);
            HarborGitOperationRequest request = NewRequest(HarborGitOperationEnum.TryCountCommitsBetween, repoPath);
            request.FromCommit = fromRef;
            request.ToCommit = toRef;
            return SendValueAsync(repoPath, request, token, result => result.Number);
        }

        /// <inheritdoc />
        public Task PullAsync(string workingDirectory, CancellationToken token = default)
        {
            if (!IsRemote(workingDirectory)) return _Local.PullAsync(workingDirectory, token);
            return SendVoidAsync(workingDirectory, NewRequest(HarborGitOperationEnum.Pull, workingDirectory), token);
        }

        /// <inheritdoc />
        public Task PullFastForwardOnlyAsync(string workingDirectory, CancellationToken token = default)
        {
            if (!IsRemote(workingDirectory)) return _Local.PullFastForwardOnlyAsync(workingDirectory, token);
            return SendVoidAsync(workingDirectory, NewRequest(HarborGitOperationEnum.PullFastForwardOnly, workingDirectory), token);
        }

        /// <inheritdoc />
        public Task<string?> GetCurrentBranchAsync(string workingDirectory, CancellationToken token = default)
        {
            if (!IsRemote(workingDirectory)) return _Local.GetCurrentBranchAsync(workingDirectory, token);
            return SendValueAsync(workingDirectory, NewRequest(HarborGitOperationEnum.GetCurrentBranch, workingDirectory), token, result => result.Text);
        }

        /// <inheritdoc />
        public Task<bool> IsWorkingDirectoryCleanAsync(string workingDirectory, CancellationToken token = default)
        {
            if (!IsRemote(workingDirectory)) return _Local.IsWorkingDirectoryCleanAsync(workingDirectory, token);
            return SendValueAsync(workingDirectory, NewRequest(HarborGitOperationEnum.IsWorkingDirectoryClean, workingDirectory), token,
                result => result.Flag ?? throw MissingResult("flag"));
        }

        /// <inheritdoc />
        public Task<bool> HasUncommittedTrackedChangesAsync(string workingDirectory, CancellationToken token = default)
        {
            if (!IsRemote(workingDirectory)) return _Local.HasUncommittedTrackedChangesAsync(workingDirectory, token);
            return SendValueAsync(workingDirectory, NewRequest(HarborGitOperationEnum.HasUncommittedTrackedChanges, workingDirectory), token,
                result => result.Flag ?? throw MissingResult("flag"));
        }

        /// <inheritdoc />
        public Task<string> DiffAsync(string worktreePath, string baseBranch = "main", CancellationToken token = default)
        {
            if (!IsRemote(worktreePath)) return _Local.DiffAsync(worktreePath, baseBranch, token);
            HarborGitOperationRequest request = NewRequest(HarborGitOperationEnum.Diff, worktreePath);
            request.BaseBranch = baseBranch;
            return SendValueAsync(worktreePath, request, token, result => RequireText(result, "text"));
        }

        /// <inheritdoc />
        public Task<string> DiffWorkingTreeAsync(string worktreePath, string baseBranch = "main", CancellationToken token = default)
        {
            if (!IsRemote(worktreePath)) return _Local.DiffWorkingTreeAsync(worktreePath, baseBranch, token);
            HarborGitOperationRequest request = NewRequest(HarborGitOperationEnum.DiffWorkingTree, worktreePath);
            request.BaseBranch = baseBranch;
            return SendValueAsync(worktreePath, request, token, result => RequireText(result, "text"));
        }

        /// <inheritdoc />
        public Task<string?> GetHeadCommitHashAsync(string worktreePath, CancellationToken token = default)
        {
            if (!IsRemote(worktreePath)) return _Local.GetHeadCommitHashAsync(worktreePath, token);
            return SendValueAsync(worktreePath, NewRequest(HarborGitOperationEnum.GetHeadCommitHash, worktreePath), token, result => result.Text);
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<string>> GetConflictedFilesAsync(string worktreePath, CancellationToken token = default)
        {
            if (!IsRemote(worktreePath)) return _Local.GetConflictedFilesAsync(worktreePath, token);
            return SendValueAsync<IReadOnlyList<string>>(worktreePath, NewRequest(HarborGitOperationEnum.GetConflictedFiles, worktreePath), token,
                result => result.Paths);
        }

    }
}
