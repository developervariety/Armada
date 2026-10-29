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
        public Task<string?> ReadFileExcerptOnRevisionAsync(string worktreePath, string revision, string relativePath, int line, int maxLines, CancellationToken token = default)
        {
            if (!IsRemote(worktreePath)) return _Local.ReadFileExcerptOnRevisionAsync(worktreePath, revision, relativePath, line, maxLines, token);
            HarborGitOperationRequest request = NewRequest(HarborGitOperationEnum.ReadFileExcerptOnRevision, worktreePath);
            request.Revision = revision;
            request.RelativePath = relativePath;
            request.Line = line;
            request.MaxLines = maxLines;
            return SendValueAsync(worktreePath, request, token, result => result.Text);
        }

        /// <inheritdoc />
        public Task<bool?> TryIsAncestorAsync(string repoPath, string ancestorRef, string descendantRef, CancellationToken token = default)
        {
            if (!IsRemote(repoPath)) return _Local.TryIsAncestorAsync(repoPath, ancestorRef, descendantRef, token);
            HarborGitOperationRequest request = NewRequest(HarborGitOperationEnum.TryIsAncestor, repoPath);
            request.AncestorRef = ancestorRef;
            request.DescendantRef = descendantRef;
            return SendValueAsync(repoPath, request, token, result => result.Flag);
        }

        /// <inheritdoc />
        /// <remarks>A Harbor checkout has no trailer-search operation, so a Harbor address finds no commit.</remarks>
        public Task<IReadOnlyList<string>> FindCommitsByTrailerAsync(string repoPath, string revision, string trailerKey, string trailerValue, DateTime? sinceUtc, int maxCount, CancellationToken token = default)
        {
            if (!IsRemote(repoPath)) return _Local.FindCommitsByTrailerAsync(repoPath, revision, trailerKey, trailerValue, sinceUtc, maxCount, token);
            return Task.FromResult<IReadOnlyList<string>>(new List<string>());
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<GitAnchorCommit>> GetCommitsTouchingPathAsync(string worktreePath, string relativePath, int maxCount, CancellationToken token = default)
        {
            if (!IsRemote(worktreePath)) return _Local.GetCommitsTouchingPathAsync(worktreePath, relativePath, maxCount, token);
            HarborGitOperationRequest request = NewRequest(HarborGitOperationEnum.GetCommitsTouchingPath, worktreePath);
            request.RelativePath = relativePath;
            request.MaxCount = maxCount;
            return SendValueAsync<IReadOnlyList<GitAnchorCommit>>(worktreePath, request, token, result => result.Commits);
        }

        /// <inheritdoc />
        public Task<bool> PathExistsOnRevisionAsync(string worktreePath, string revision, string relativePath, CancellationToken token = default)
        {
            if (!IsRemote(worktreePath)) return _Local.PathExistsOnRevisionAsync(worktreePath, revision, relativePath, token);
            HarborGitOperationRequest request = NewRequest(HarborGitOperationEnum.PathExistsOnRevision, worktreePath);
            request.Revision = revision;
            request.RelativePath = relativePath;
            return SendValueAsync(worktreePath, request, token, result => result.Flag ?? throw MissingResult("flag"));
        }

        /// <inheritdoc />
        public Task<string?> ResolveTrackedPathSuffixAsync(string worktreePath, string revision, string relativePath, CancellationToken token = default)
        {
            if (!IsRemote(worktreePath)) return _Local.ResolveTrackedPathSuffixAsync(worktreePath, revision, relativePath, token);
            HarborGitOperationRequest request = NewRequest(HarborGitOperationEnum.ResolveTrackedPathSuffix, worktreePath);
            request.Revision = revision;
            request.RelativePath = relativePath;
            return SendValueAsync(worktreePath, request, token, result => result.Text);
        }

        /// <inheritdoc />
        public Task<List<string>> ListTrackedPathCandidatesAsync(string worktreePath, string revision, string relativePath, CancellationToken token = default)
        {
            if (!IsRemote(worktreePath)) return _Local.ListTrackedPathCandidatesAsync(worktreePath, revision, relativePath, token);
            HarborGitOperationRequest request = NewRequest(HarborGitOperationEnum.ListTrackedPathCandidates, worktreePath);
            request.Revision = revision;
            request.RelativePath = relativePath;
            return SendValueAsync<List<string>>(worktreePath, request, token, result => result.Paths);
        }

        /// <inheritdoc />
        public Task<GitAnchorPriorArt> SearchTrackedContentAsync(string worktreePath, string term, int maxSamples, CancellationToken token = default)
        {
            if (!IsRemote(worktreePath)) return _Local.SearchTrackedContentAsync(worktreePath, term, maxSamples, token);
            HarborGitOperationRequest request = NewRequest(HarborGitOperationEnum.SearchTrackedContent, worktreePath);
            request.Term = term;
            request.MaxSamples = maxSamples;
            return SendValueAsync(worktreePath, request, token, result => result.PriorArt ?? throw MissingResult("prior_art"));
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<string>> GetChangedFilesSinceAsync(string worktreePath, string startCommit, CancellationToken token = default)
        {
            if (!IsRemote(worktreePath)) return _Local.GetChangedFilesSinceAsync(worktreePath, startCommit, token);
            HarborGitOperationRequest request = NewRequest(HarborGitOperationEnum.GetChangedFilesSince, worktreePath);
            request.StartCommit = startCommit;
            return SendValueAsync<IReadOnlyList<string>>(worktreePath, request, token, result => result.Paths);
        }

    }
}
