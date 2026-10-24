namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Harbor;
    using Armada.Core.Models;

    /// <summary>Routes Git reads and bounded evidence queries.</summary>
    public sealed partial class HostAwareGitService
    {
        /// <inheritdoc />
        public async Task<ChangedPathsRead> GetChangedFilePathsAgainstBaseAsync(string worktreePath, string baseBranch = "main", CancellationToken token = default)
        {
            if (!IsRemote(worktreePath)) return await _Local.GetChangedFilePathsAgainstBaseAsync(worktreePath, baseBranch, token).ConfigureAwait(false);
            try
            {
                HarborGitOperationRequest request = NewRequest(HarborGitOperationEnum.GetChangedFilePathsAgainstBase, worktreePath);
                request.BaseBranch = baseBranch;
                return await SendValueAsync(worktreePath, request, token,
                    result => result.ChangedPaths ?? throw MissingResult("changed_paths")).ConfigureAwait(false);
            }
            catch (Exception exception) when (!token.IsCancellationRequested)
            {
                return ChangedPathsRead.Unavailable("could not read changed paths against " + baseBranch + " in " + worktreePath
                    + ": " + exception.GetType().Name + ": " + exception.Message);
            }
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<string>> ReadChangedPathsAgainstBaseAsync(string worktreePath, string baseBranch = "main", CancellationToken token = default)
        {
            if (!IsRemote(worktreePath)) return _Local.ReadChangedPathsAgainstBaseAsync(worktreePath, baseBranch, token);
            HarborGitOperationRequest request = NewRequest(HarborGitOperationEnum.ReadChangedPathsAgainstBase, worktreePath);
            request.BaseBranch = baseBranch;
            return SendValueAsync<IReadOnlyList<string>>(worktreePath, request, token, result => result.Paths);
        }

        /// <inheritdoc />
        public Task<ChangedPathsRead> ReadChangedPathsBetweenCommitsAsync(string repoPath, string fromCommit, string toCommit, CancellationToken token = default)
        {
            if (!IsRemote(repoPath)) return _Local.ReadChangedPathsBetweenCommitsAsync(repoPath, fromCommit, toCommit, token);
            HarborGitOperationRequest request = NewRequest(HarborGitOperationEnum.ReadChangedPathsBetweenCommits, repoPath);
            request.FromCommit = fromCommit;
            request.ToCommit = toCommit;
            return SendValueAsync(repoPath, request, token, result => result.ChangedPaths ?? throw MissingResult("changed_paths"));
        }

        /// <inheritdoc />
        public Task<ChangedPathStatusRead> ReadChangedPathStatusesBetweenCommitsAsync(string repoPath, string fromCommit, string toCommit, int maxPaths, TimeSpan timeout, int outputLimitBytes, CancellationToken token = default)
        {
            if (!IsRemote(repoPath)) return _Local.ReadChangedPathStatusesBetweenCommitsAsync(repoPath, fromCommit, toCommit, maxPaths, timeout, outputLimitBytes, token);
            HarborGitOperationRequest request = NewRequest(HarborGitOperationEnum.ReadChangedPathStatusesBetweenCommits, repoPath);
            request.FromCommit = fromCommit;
            request.ToCommit = toCommit;
            request.MaxCount = maxPaths;
            request.TimeoutMilliseconds = checked((int)timeout.TotalMilliseconds);
            request.OutputLimitBytes = outputLimitBytes;
            return SendValueAsync(repoPath, request, token, result => result.ChangedPathStatuses ?? throw MissingResult("changed_path_statuses"));
        }
    }
}
