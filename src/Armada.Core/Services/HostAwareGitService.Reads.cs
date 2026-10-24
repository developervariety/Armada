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
        public Task<bool> IsRepositoryAsync(string path, CancellationToken token = default)
        {
            if (!IsRemote(path)) return _Local.IsRepositoryAsync(path, token);
            return SendValueAsync(path, NewRequest(HarborGitOperationEnum.IsRepository, path), token,
                result => result.Flag ?? throw MissingResult("flag"));
        }

        /// <inheritdoc />
        public Task<string> GetRepositoryHeadRefAsync(string repoPath, CancellationToken token = default)
        {
            if (!IsRemote(repoPath)) return _Local.GetRepositoryHeadRefAsync(repoPath, token);
            return SendValueAsync(repoPath, NewRequest(HarborGitOperationEnum.GetRepositoryHeadRef, repoPath), token,
                result => RequireText(result, "text"));
        }

        /// <inheritdoc />
        public Task<string?> GetRevisionShaAsync(string repoPath, string revision, CancellationToken token = default)
        {
            if (!IsRemote(repoPath)) return _Local.GetRevisionShaAsync(repoPath, revision, token);
            HarborGitOperationRequest request = NewRequest(HarborGitOperationEnum.GetRevisionSha, repoPath);
            request.Revision = revision;
            return SendValueAsync(repoPath, request, token, result => result.Text);
        }

        /// <inheritdoc />
        public Task<string?> GetRevisionCommitShaAsync(string repoPath, string revision, CancellationToken token = default)
        {
            if (!IsRemote(repoPath)) return _Local.GetRevisionCommitShaAsync(repoPath, revision, token);
            HarborGitOperationRequest request = NewRequest(HarborGitOperationEnum.GetRevisionCommitSha, repoPath);
            request.Revision = revision;
            return SendValueAsync(repoPath, request, token, result => result.Text);
        }

        /// <inheritdoc />
        public Task<DateTime?> GetCommitTimeUtcAsync(string repoPath, string revision, CancellationToken token = default)
        {
            if (!IsRemote(repoPath)) return _Local.GetCommitTimeUtcAsync(repoPath, revision, token);
            HarborGitOperationRequest request = NewRequest(HarborGitOperationEnum.GetCommitTimeUtc, repoPath);
            request.Revision = revision;
            return SendValueAsync(repoPath, request, token, result => result.TimeUtc);
        }

        /// <inheritdoc />
        public Task<string?> ResolveAnchorPathOnRevisionAsync(string worktreePath, string revision, string relativePath, CancellationToken token = default)
        {
            if (!IsRemote(worktreePath)) return _Local.ResolveAnchorPathOnRevisionAsync(worktreePath, revision, relativePath, token);
            HarborGitOperationRequest request = NewRequest(HarborGitOperationEnum.ResolveAnchorPathOnRevision, worktreePath);
            request.Revision = revision;
            request.RelativePath = relativePath;
            return SendValueAsync(worktreePath, request, token, result => result.Text);
        }

        /// <inheritdoc />
        public Task<TrackedPathMatch> MatchAnchorPathOnRevisionAsync(string worktreePath, string revision, string relativePath, CancellationToken token = default)
        {
            if (!IsRemote(worktreePath)) return _Local.MatchAnchorPathOnRevisionAsync(worktreePath, revision, relativePath, token);
            HarborGitOperationRequest request = NewRequest(HarborGitOperationEnum.MatchAnchorPathOnRevision, worktreePath);
            request.Revision = revision;
            request.RelativePath = relativePath;
            return SendValueAsync(worktreePath, request, token, result => result.AnchorPath ?? throw MissingResult("anchor_path"));
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<string>?> ReadFileLinesOnRevisionAsync(string worktreePath, string revision, string relativePath, CancellationToken token = default)
        {
            if (!IsRemote(worktreePath)) return _Local.ReadFileLinesOnRevisionAsync(worktreePath, revision, relativePath, token);
            HarborGitOperationRequest request = NewRequest(HarborGitOperationEnum.ReadFileLinesOnRevision, worktreePath);
            request.Revision = revision;
            request.RelativePath = relativePath;
            return SendValueAsync<IReadOnlyList<string>?>(worktreePath, request, token, result => result.Flag == true ? result.Paths : null);
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<GitAnchorCommit>> GetCommitsTouchingPathOnRevisionAsync(string worktreePath, string revision, string relativePath, int maxCount, CancellationToken token = default)
        {
            if (!IsRemote(worktreePath)) return _Local.GetCommitsTouchingPathOnRevisionAsync(worktreePath, revision, relativePath, maxCount, token);
            HarborGitOperationRequest request = NewRequest(HarborGitOperationEnum.GetCommitsTouchingPathOnRevision, worktreePath);
            request.Revision = revision;
            request.RelativePath = relativePath;
            request.MaxCount = maxCount;
            return SendValueAsync<IReadOnlyList<GitAnchorCommit>>(worktreePath, request, token, result => result.Commits);
        }

        /// <inheritdoc />
        public Task<GitAnchorPriorArt> SearchTrackedContentOnRevisionAsync(string worktreePath, string revision, string term, int maxSamples, CancellationToken token = default)
        {
            if (!IsRemote(worktreePath)) return _Local.SearchTrackedContentOnRevisionAsync(worktreePath, revision, term, maxSamples, token);
            HarborGitOperationRequest request = NewRequest(HarborGitOperationEnum.SearchTrackedContentOnRevision, worktreePath);
            request.Revision = revision;
            request.Term = term;
            request.MaxSamples = maxSamples;
            return SendValueAsync(worktreePath, request, token, result => result.PriorArt ?? throw MissingResult("prior_art"));
        }

    }
}
