namespace Armada.Runtimes.Harbor.Workspace.Git
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Harbor;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;

    public static partial class HarborGitDispatcher
    {
        private static async Task<HarborGitOperationResult?> ExecuteReadAsync(
            IGitService git, string checkoutPath, string repositoryPath, string? targetCheckoutPath,
            HarborGitOperationRequest request, CancellationToken token)
        {
            string path;
            string selectedPath = SelectPath(request, checkoutPath, repositoryPath, true);
            switch (request.Operation)
            {
                case HarborGitOperationEnum.IsRepository:
                    return await FlagAsync(git.IsRepositoryAsync(selectedPath, token)).ConfigureAwait(false);
                case HarborGitOperationEnum.IsWorkingDirectoryClean:
                    return await FlagAsync(git.IsWorkingDirectoryCleanAsync(selectedPath, token)).ConfigureAwait(false);
                case HarborGitOperationEnum.HasUncommittedTrackedChanges:
                    return await FlagAsync(git.HasUncommittedTrackedChangesAsync(selectedPath, token)).ConfigureAwait(false);
                case HarborGitOperationEnum.Diff:
                    return await TextAsync(git.DiffAsync(selectedPath, String.IsNullOrWhiteSpace(request.BaseBranch) ? "main" : RequiredRevision(request.BaseBranch), token)).ConfigureAwait(false);
                case HarborGitOperationEnum.DiffWorkingTree:
                    return await TextAsync(git.DiffWorkingTreeAsync(selectedPath, String.IsNullOrWhiteSpace(request.BaseBranch) ? "main" : RequiredRevision(request.BaseBranch), token)).ConfigureAwait(false);
                case HarborGitOperationEnum.GetHeadCommitHash:
                    return await NullableTextAsync(git.GetHeadCommitHashAsync(selectedPath, token)).ConfigureAwait(false);
                case HarborGitOperationEnum.GetConflictedFiles:
                    return await PathsAsync(git.GetConflictedFilesAsync(selectedPath, token)).ConfigureAwait(false);
                case HarborGitOperationEnum.GetRevisionSha:
                    return await NullableTextAsync(git.GetRevisionShaAsync(selectedPath, RequiredRevision(request.Revision), token)).ConfigureAwait(false);
                case HarborGitOperationEnum.GetRevisionCommitSha:
                    return await NullableTextAsync(git.GetRevisionCommitShaAsync(selectedPath, RequiredRevision(request.Revision), token)).ConfigureAwait(false);
                case HarborGitOperationEnum.GetCommitTimeUtc:
                    return await TimeAsync(git.GetCommitTimeUtcAsync(selectedPath, RequiredRevision(request.Revision), token)).ConfigureAwait(false);
                case HarborGitOperationEnum.ResolveAnchorPathOnRevision:
                    return await NullableTextAsync(git.ResolveAnchorPathOnRevisionAsync(selectedPath, RequiredRevision(request.Revision), RequiredRelativePath(request.RelativePath), token)).ConfigureAwait(false);
                case HarborGitOperationEnum.MatchAnchorPathOnRevision:
                    return new HarborGitOperationResult { AnchorPath = await git.MatchAnchorPathOnRevisionAsync(
                        selectedPath, RequiredRevision(request.Revision), RequiredRelativePath(request.RelativePath), token).ConfigureAwait(false) };
                case HarborGitOperationEnum.ReadFileLinesOnRevision:
                    IReadOnlyList<string>? lines = await git.ReadFileLinesOnRevisionAsync(selectedPath,
                        RequiredRevision(request.Revision), RequiredRelativePath(request.RelativePath), token).ConfigureAwait(false);
                    return new HarborGitOperationResult { Flag = lines != null, Paths = lines == null ? new List<string>() : new List<string>(lines) };
                case HarborGitOperationEnum.GetCommitsTouchingPathOnRevision:
                    return new HarborGitOperationResult { Commits = new List<GitAnchorCommit>(await git.GetCommitsTouchingPathOnRevisionAsync(
                        selectedPath, RequiredRevision(request.Revision), RequiredRelativePath(request.RelativePath), request.MaxCount, token).ConfigureAwait(false)) };
                case HarborGitOperationEnum.SearchTrackedContentOnRevision:
                    return new HarborGitOperationResult { PriorArt = await git.SearchTrackedContentOnRevisionAsync(
                        selectedPath, RequiredRevision(request.Revision), Required(request.Term, "term", 512), request.MaxSamples, token).ConfigureAwait(false) };
                case HarborGitOperationEnum.ReadFileExcerptOnRevision:
                    return await NullableTextAsync(git.ReadFileExcerptOnRevisionAsync(selectedPath, RequiredRevision(request.Revision),
                        RequiredRelativePath(request.RelativePath), request.Line, request.MaxLines, token)).ConfigureAwait(false);
                case HarborGitOperationEnum.TryIsAncestor:
                    return await NullableFlagAsync(git.TryIsAncestorAsync(selectedPath,
                        RequiredRevision(request.AncestorRef), RequiredRevision(request.DescendantRef), token)).ConfigureAwait(false);
                case HarborGitOperationEnum.GetCommitsTouchingPath:
                    return new HarborGitOperationResult { Commits = new List<GitAnchorCommit>(await git.GetCommitsTouchingPathAsync(
                        selectedPath, RequiredRelativePath(request.RelativePath), request.MaxCount, token).ConfigureAwait(false)) };
                case HarborGitOperationEnum.PathExistsOnRevision:
                    return await FlagAsync(git.PathExistsOnRevisionAsync(selectedPath, RevisionOrHead(request.Revision), RequiredRelativePath(request.RelativePath), token)).ConfigureAwait(false);
                case HarborGitOperationEnum.ResolveTrackedPathSuffix:
                    return await NullableTextAsync(git.ResolveTrackedPathSuffixAsync(selectedPath, RevisionOrHead(request.Revision), RequiredRelativePath(request.RelativePath), token)).ConfigureAwait(false);
                case HarborGitOperationEnum.ListTrackedPathCandidates:
                    return await ListPathsAsync(git.ListTrackedPathCandidatesAsync(selectedPath, RevisionOrHead(request.Revision), RequiredRelativePath(request.RelativePath), token)).ConfigureAwait(false);
                case HarborGitOperationEnum.SearchTrackedContent:
                    return new HarborGitOperationResult { PriorArt = await git.SearchTrackedContentAsync(
                        selectedPath, Required(request.Term, "term", 512), request.MaxSamples, token).ConfigureAwait(false) };
                case HarborGitOperationEnum.GetChangedFilesSince:
                    return await PathsAsync(git.GetChangedFilesSinceAsync(selectedPath, RequiredCommit(request.StartCommit, "start_commit"), token)).ConfigureAwait(false);
                case HarborGitOperationEnum.GetChangedFilePathsAgainstBase:
                    return await ChangedPathsAsync(git.GetChangedFilePathsAgainstBaseAsync(selectedPath,
                        String.IsNullOrWhiteSpace(request.BaseBranch) ? "main" : RequiredRevision(request.BaseBranch), token)).ConfigureAwait(false);
                case HarborGitOperationEnum.ReadChangedPathsBetweenCommits:
                    return await ChangedPathsAsync(git.ReadChangedPathsBetweenCommitsAsync(selectedPath,
                        RequiredCommit(request.FromCommit, "from_commit"), RequiredCommit(request.ToCommit, "to_commit"), token)).ConfigureAwait(false);
                case HarborGitOperationEnum.ReadChangedPathStatusesBetweenCommits:
                    return await ChangedStatusAsync(git.ReadChangedPathStatusesBetweenCommitsAsync(selectedPath,
                        RequiredCommit(request.FromCommit, "from_commit"), RequiredCommit(request.ToCommit, "to_commit"),
                        request.MaxCount, TimeSpan.FromMilliseconds(request.TimeoutMilliseconds), request.OutputLimitBytes, token)).ConfigureAwait(false);
                case HarborGitOperationEnum.ReadChangedPathsAgainstBase:
                    return await PathsAsync(git.ReadChangedPathsAgainstBaseAsync(selectedPath,
                        String.IsNullOrWhiteSpace(request.BaseBranch) ? "main" : RequiredRevision(request.BaseBranch), token)).ConfigureAwait(false);
                case HarborGitOperationEnum.IsPrMerged:
                    return await FlagAsync(git.IsPrMergedAsync(selectedPath, RequiredHttpUrl(request.PullRequestUrl), token)).ConfigureAwait(false);
                case HarborGitOperationEnum.IsPathTracked:
                    return await FlagAsync(git.IsPathTrackedAsync(selectedPath, RequiredRelativePath(request.RelativePath), token)).ConfigureAwait(false);
                case HarborGitOperationEnum.BranchExists:
                    return await FlagAsync(git.BranchExistsAsync(selectedPath, RequiredBranch(request.BranchName), token)).ConfigureAwait(false);
                case HarborGitOperationEnum.IsWorktreeRegistered:
                    path = selectedPath;
                    return await FlagAsync(git.IsWorktreeRegisteredAsync(path, targetCheckoutPath ?? checkoutPath, token)).ConfigureAwait(false);
                case HarborGitOperationEnum.GetCommitCountBetween:
                    return await NumberAsync(git.GetCommitCountBetweenAsync(selectedPath,
                        RequiredRevision(request.FromCommit), RequiredRevision(request.ToCommit), token)).ConfigureAwait(false);
                case HarborGitOperationEnum.TryCountCommitsBetween:
                    return await NullableNumberAsync(git.TryCountCommitsBetweenAsync(selectedPath,
                        RequiredRevision(request.FromCommit), RequiredRevision(request.ToCommit), token)).ConfigureAwait(false);
                case HarborGitOperationEnum.GetCurrentBranch:
                    return await NullableTextAsync(git.GetCurrentBranchAsync(selectedPath, token)).ConfigureAwait(false);
                case HarborGitOperationEnum.GetRepositoryHeadRef:
                    return await TextAsync(git.GetRepositoryHeadRefAsync(selectedPath, token)).ConfigureAwait(false);
                default:
                    return null;
            }
        }

        private static string RevisionOrHead(string? value) =>
            String.IsNullOrWhiteSpace(value) ? "HEAD" : RequiredRevision(value);
    }
}
