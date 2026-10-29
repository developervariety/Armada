namespace Test.Shared.Infrastructure
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;

    /// <summary>
    /// Stub git service for testing that records calls but does not execute git, with
    /// result controls and failure-injection flags so suites can exercise both success and
    /// error paths deterministically without a real repository. Where a caller reads a value
    /// every real repository has (the commit behind HEAD or a branch tip, the repository HEAD
    /// after it is set, whether a term is present on a revision), the stub answers as git does.
    /// </summary>
    public class StubGitService : IGitService
    {
        // Call tracking
        public List<string> CloneCalls { get; } = new List<string>();
        public List<string> WorktreeCalls { get; } = new List<string>();

        /// <summary>
        /// Awaited inside CreateWorktreeAsync after the call is recorded, so a test can hold a
        /// provisioning pass open while another pass runs.
        /// </summary>
        public Func<Task>? BeforeWorktreeCreate { get; set; }
        public List<string> DeleteBranchCalls { get; } = new List<string>();
        public List<string> RemoveWorktreeCalls { get; } = new List<string>();
        public List<string> MergeBranchCalls { get; } = new List<string>();
        public List<string> PushCalls { get; } = new List<string>();
        public List<string> PrCalls { get; } = new List<string>();
        public List<string> PullCalls { get; } = new List<string>();
        public List<string> PullFastForwardOnlyCalls { get; } = new List<string>();
        public List<string> PruneWorktreeCalls { get; } = new List<string>();
        public List<string> DiffCalls { get; } = new List<string>();
        public List<string> LiveDiffCalls { get; } = new List<string>();
        public List<string> RepositoryHeadCalls { get; } = new List<string>();
        public List<string> OperationCalls { get; } = new List<string>();

        // Result controls
        public bool IsRepositoryResult { get; set; } = true;
        public bool IsPrMergedResult { get; set; } = true;
        public string CreatePrResult { get; set; } = "https://github.com/test/repo/pull/1";
        public string DiffResult { get; set; } = "";
        /// <summary>Optional real Git reader for public-surface repository integration tests.</summary>
        public IGitService? DiffReader { get; set; }

        public string RepositoryHeadRefResult { get; set; } = "refs/heads/main";
        public string? CurrentBranchResult { get; set; } = "main";
        public bool IsWorkingDirectoryCleanResult { get; set; } = true;
        public IReadOnlyList<string> ChangedFilesSinceResult { get; set; } = Array.Empty<string>();
        public HashSet<string> TrackedPaths { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public bool DefaultBranchExistsResult { get; set; } = true;
        public HashSet<string> ExistingBranches { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "main" };

        // Failure injection
        public bool ShouldThrowOnWorktree { get; set; } = false;

        /// <summary>Message thrown when ShouldThrowOnWorktree is set; null uses the generic message.</summary>
        public string? WorktreeFailureMessage { get; set; } = null;

        /// <summary>Recorded compare-and-swap ref moves as "repoPath:branch:newSha:expectedOldSha".</summary>
        public List<string> CompareAndSwapCalls { get; } = new List<string>();

        public Task CompareAndSwapBranchRefAsync(string repoPath, string branchName, string newSha, string expectedOldSha, CancellationToken token = default)
        {
            CompareAndSwapCalls.Add(repoPath + ":" + branchName + ":" + newSha + ":" + expectedOldSha);
            OperationCalls.Add("cas-ref:" + branchName);
            return Task.CompletedTask;
        }
        public bool ShouldThrowOnPush { get; set; } = false;
        public int DriftPushFailuresRemaining { get; set; } = 0;
        public bool ShouldThrowOnCreatePr { get; set; } = false;
        public bool ShouldThrowOnMergeLocal { get; set; } = false;
        public bool ShouldThrowOnDeleteBranch { get; set; } = false;
        public bool ShouldThrowOnSetHeadSymbolicRef { get; set; } = false;
        public bool ShouldThrowOnDiff { get; set; } = false;

        public Task CloneBareAsync(string repoUrl, string localPath, CancellationToken token = default)
        {
            CloneCalls.Add(repoUrl + " -> " + localPath);
            OperationCalls.Add("clone:" + localPath);
            return Task.CompletedTask;
        }

        /// <summary>
        /// When true, CreateWorktreeAsync also creates the worktree directory on disk, so a gate
        /// that runs a real command in the provisioned worktree has a working directory to run in.
        /// Defaults to false, preserving the record-only behavior for every existing caller.
        /// </summary>
        public bool CreateWorktreeDirectories { get; set; } = false;

        public async Task CreateWorktreeAsync(string repoPath, string worktreePath, string branchName, string baseBranch = "main", bool detached = false, CancellationToken token = default)
        {
            if (ShouldThrowOnWorktree) throw new InvalidOperationException(WorktreeFailureMessage ?? "Simulated worktree failure");
            ExistingBranches.Add(branchName);
            WorktreeCalls.Add(worktreePath);
            if (CreateWorktreeDirectories) System.IO.Directory.CreateDirectory(worktreePath);
            if (BeforeWorktreeCreate != null) await BeforeWorktreeCreate().ConfigureAwait(false);
            OperationCalls.Add("create-worktree:" + worktreePath);
        }

        public Task RemoveWorktreeAsync(string worktreePath, CancellationToken token = default)
        {
            RemoveWorktreeCalls.Add(worktreePath);
            OperationCalls.Add("remove-worktree:" + worktreePath);
            return Task.CompletedTask;
        }
        public List<string> FetchCalls { get; } = new List<string>();

        public Task FetchAsync(string repoPath, CancellationToken token = default)
        {
            FetchCalls.Add(repoPath);
            return Task.CompletedTask;
        }

        /// <summary>Commits the trailer search returns, keyed by <c>trailerValue</c>; no entry means no match.</summary>
        public Dictionary<string, string> TrailerCommits { get; } = new Dictionary<string, string>(StringComparer.Ordinal);
        public List<string> TrailerSearchCalls { get; } = new List<string>();

        public Task<IReadOnlyList<string>> FindCommitsByTrailerAsync(
            string repoPath, string revision, string trailerKey, string trailerValue, DateTime? sinceUtc, int maxCount, CancellationToken token = default)
        {
            TrailerSearchCalls.Add(repoPath + ":" + revision + ":" + trailerKey + ":" + trailerValue);
            List<string> commits = new List<string>();
            if (TrailerCommits.TryGetValue(trailerValue, out string? commit)) commits.Add(commit);
            return Task.FromResult<IReadOnlyList<string>>(commits);
        }

        public Task PushBranchAsync(string worktreePath, string remoteName = "origin", CancellationToken token = default)
        {
            if (DriftPushFailuresRemaining > 0)
            {
                DriftPushFailuresRemaining--;
                OperationCalls.Add("push-drift:" + worktreePath);
                throw new InvalidOperationException("target branch drift: non-fast-forward update rejected");
            }
            if (ShouldThrowOnPush) throw new InvalidOperationException("Simulated push failure");
            PushCalls.Add(worktreePath);
            OperationCalls.Add("push:" + worktreePath);
            return Task.CompletedTask;
        }

        public Task<string> CreatePullRequestAsync(string worktreePath, string title, string body, CancellationToken token = default)
        {
            if (ShouldThrowOnCreatePr) throw new InvalidOperationException("Simulated PR creation failure");
            PrCalls.Add(title);
            OperationCalls.Add("create-pr:" + title);
            return Task.FromResult(CreatePrResult);
        }

        public Task RepairWorktreeAsync(string worktreePath, CancellationToken token = default) => Task.CompletedTask;
        public Task<bool> IsRepositoryAsync(string path, CancellationToken token = default) => Task.FromResult(IsRepositoryResult);

        public Task DeleteLocalBranchAsync(string repoPath, string branchName, CancellationToken token = default, [System.Runtime.CompilerServices.CallerMemberName] string caller = "")
        {
            if (ShouldThrowOnDeleteBranch) throw new InvalidOperationException("Simulated branch delete failure");
            DeleteBranchCalls.Add(repoPath + ":" + branchName);
            OperationCalls.Add("delete-local-branch:" + branchName);
            return Task.CompletedTask;
        }

        public Task DeleteRemoteBranchAsync(string repoPath, string branchName, CancellationToken token = default, [System.Runtime.CompilerServices.CallerMemberName] string caller = "")
        {
            DeleteBranchCalls.Add("remote:" + branchName);
            OperationCalls.Add("delete-remote-branch:" + branchName);
            return Task.CompletedTask;
        }

        public Task PushRefSpecAsync(string repoPath, string srcRef, string destRef, CancellationToken token = default)
        {
            if (ShouldThrowOnPush) throw new InvalidOperationException("Simulated push failure");
            PushCalls.Add(repoPath + ":" + srcRef + ":" + destRef);
            OperationCalls.Add("push-refspec:" + srcRef + ":" + destRef);
            return Task.CompletedTask;
        }

        /// <summary>
        /// Simulate a failed ref copy, so a caller that must not delete unpreserved work can be tested.
        /// </summary>
        public bool ShouldThrowOnCopyRef { get; set; } = false;

        public Task CopyRefAsync(string repoPath, string srcRef, string destRef, CancellationToken token = default)
        {
            if (ShouldThrowOnCopyRef) throw new InvalidOperationException("Simulated ref copy failure");
            OperationCalls.Add("copy-ref:" + srcRef + ":" + destRef);
            return Task.CompletedTask;
        }

        public List<string> ForceUpdateBranchRefCalls { get; } = new List<string>();

        public Task ForceUpdateBranchRefAsync(string repoPath, string branchName, string commitSha, CancellationToken token = default)
        {
            ForceUpdateBranchRefCalls.Add(repoPath + ":" + branchName + ":" + commitSha);
            OperationCalls.Add("force-update-ref:" + branchName + ":" + commitSha);
            return Task.CompletedTask;
        }

        public Task<string> GetRepositoryHeadRefAsync(string repoPath, CancellationToken token = default)
        {
            RepositoryHeadCalls.Add("get-head:" + repoPath);
            OperationCalls.Add("get-head:" + repoPath);
            return Task.FromResult(RepositoryHeadRefResult);
        }

        public Task SetRepositoryHeadAsync(string repoPath, string branchName, CancellationToken token = default)
        {
            RepositoryHeadRefResult = "refs/heads/" + branchName;
            RepositoryHeadCalls.Add("set-head:" + repoPath + ":" + branchName);
            OperationCalls.Add("set-head:" + branchName);
            return Task.CompletedTask;
        }

        public Task PruneWorktreesAsync(string repoPath, CancellationToken token = default)
        {
            PruneWorktreeCalls.Add(repoPath);
            OperationCalls.Add("prune-worktrees:" + repoPath);
            return Task.CompletedTask;
        }
        public Task EnableAutoMergeAsync(string worktreePath, string prUrl, CancellationToken token = default) => Task.CompletedTask;

        public Task MergeBranchLocalAsync(string targetWorkDir, string sourceRepoPath, string branchName, string? targetBranch = null, string? commitMessage = null, CancellationToken token = default)
        {
            MergeBranchCalls.Add(branchName + " -> " + targetWorkDir);
            OperationCalls.Add("merge-local:" + branchName);
            if (ShouldThrowOnMergeLocal) throw new InvalidOperationException("Simulated merge failure");
            return Task.CompletedTask;
        }

        public Task PullAsync(string workingDirectory, CancellationToken token = default)
        {
            PullCalls.Add(workingDirectory);
            return Task.CompletedTask;
        }

        public Task PullFastForwardOnlyAsync(string workingDirectory, CancellationToken token = default)
        {
            PullFastForwardOnlyCalls.Add(workingDirectory);
            OperationCalls.Add("pull-ff-only:" + workingDirectory);
            return Task.CompletedTask;
        }

        public Task<string?> GetCurrentBranchAsync(string workingDirectory, CancellationToken token = default)
            => Task.FromResult(CurrentBranchResult);

        public Task<bool> IsWorkingDirectoryCleanAsync(string workingDirectory, CancellationToken token = default)
            => Task.FromResult(IsWorkingDirectoryCleanResult);

        public Task<bool> HasUncommittedTrackedChangesAsync(string workingDirectory, CancellationToken token = default)
            => Task.FromResult(!IsWorkingDirectoryCleanResult);

        /// <summary>
        /// Revision SHAs keyed by "repoPath|revision", falling back to RevisionShaResult.
        /// </summary>
        public Dictionary<string, string?> RevisionShas { get; } = new Dictionary<string, string?>();

        /// <summary>Default answer for GetRevisionShaAsync when no keyed entry matches.</summary>
        public string? RevisionShaResult { get; set; } = null;

        public Task<string?> GetRevisionShaAsync(string repoPath, string revision, CancellationToken token = default)
        {
            if (RevisionShas.TryGetValue(repoPath + "|" + revision, out string? keyed)) return Task.FromResult(keyed);
            return Task.FromResult(RevisionShaResult);
        }

        /// <summary>Strict commit resolutions keyed by "repoPath|revision".</summary>
        public Dictionary<string, string?> RevisionCommitShas { get; } = new Dictionary<string, string?>();

        /// <summary>Default strict commit answer when no keyed entry matches.</summary>
        public string? RevisionCommitShaResult { get; set; } = null;

        /// <summary>Answer for HEAD and refs/heads/* when neither a keyed entry nor RevisionCommitShaResult is set.</summary>
        public string? LandingCommitShaResult { get; set; } = new string('c', 40);

        public List<string> RevisionCommitShaCalls { get; } = new List<string>();

        /// <summary>Recorded dock snapshots as "worktreePath|missionId".</summary>
        public List<string> SnapshotCalls { get; } = new List<string>();

        /// <summary>Result every dock snapshot returns.</summary>
        public DockWorkSnapshot SnapshotResult { get; set; } = DockWorkSnapshot.NoChanges();

        public Task<DockWorkSnapshot> SnapshotDockWorkAsync(string worktreePath, string missionId, string message, CancellationToken token = default)
        {
            // Like the real snapshot, a second save of the same unchanged worktree reports the branch it already wrote.
            bool repeat = SnapshotCalls.Contains(worktreePath + "|" + missionId);
            SnapshotCalls.Add(worktreePath + "|" + missionId);
            OperationCalls.Add("snapshot:" + missionId);
            if (repeat && SnapshotResult.Status == DockWorkSnapshotStatusEnum.Saved)
                return Task.FromResult(DockWorkSnapshot.AlreadySaved(SnapshotResult.RefName!, SnapshotResult.CommitSha!));
            return Task.FromResult(SnapshotResult);
        }

        /// <summary>Committer times keyed by "repoPath|revision".</summary>
        public Dictionary<string, DateTime?> CommitTimes { get; } = new Dictionary<string, DateTime?>();

        public Task<DateTime?> GetCommitTimeUtcAsync(string repoPath, string revision, CancellationToken token = default)
        {
            if (CommitTimes.TryGetValue(repoPath + "|" + revision, out DateTime? keyed)) return Task.FromResult(keyed);
            return Task.FromResult<DateTime?>(null);
        }

        public Task<string?> GetRevisionCommitShaAsync(string repoPath, string revision, CancellationToken token = default)
        {
            RevisionCommitShaCalls.Add(repoPath + "|" + revision);
            if (RevisionCommitShas.TryGetValue(repoPath + "|" + revision, out string? keyed)) return Task.FromResult(keyed);
            if (RevisionCommitShaResult == null
                && (String.Equals(revision, "HEAD", StringComparison.Ordinal) || revision.StartsWith("refs/heads/", StringComparison.Ordinal)))
            {
                // A landing resolves the target tip and the merged HEAD before it moves any ref. A
                // stub that simulates no repository still answers those two, so landing tests reach
                // the steps they assert on; keyed entries or RevisionCommitShaResult override it.
                return Task.FromResult<string?>(LandingCommitShaResult);
            }
            return Task.FromResult(RevisionCommitShaResult);
        }

        /// <summary>
        /// Ancestry answer for TryIsAncestorAsync. Null means UNKNOWN, matching the interface
        /// default: a stub that consults no repository must not be able to answer "yes".
        /// </summary>
        public bool? IsAncestorResult { get; set; } = null;
        public List<string> IsAncestorCalls { get; } = new List<string>();

        /// <summary>Ancestry answers keyed by <c>ancestor|descendant</c>; consulted before <see cref="IsAncestorResult"/>.</summary>
        public Dictionary<string, bool?> AncestorResults { get; } = new Dictionary<string, bool?>(StringComparer.Ordinal);

        public Task<bool?> TryIsAncestorAsync(
            string repoPath, string ancestorRef, string descendantRef, CancellationToken token = default)
        {
            IsAncestorCalls.Add(repoPath + ":" + ancestorRef + ":" + descendantRef);
            if (AncestorResults.TryGetValue(ancestorRef + "|" + descendantRef, out bool? keyed)) return Task.FromResult(keyed);
            return Task.FromResult(IsAncestorResult);
        }

        public Task<string> DiffAsync(string worktreePath, string baseBranch = "main", CancellationToken token = default)
        {
            if (ShouldThrowOnDiff) throw new InvalidOperationException("Simulated diff failure");
            DiffCalls.Add(worktreePath);
            return DiffReader == null ? Task.FromResult(DiffResult) : DiffReader.DiffAsync(worktreePath, baseBranch, token);
        }

        public Task<string> DiffWorkingTreeAsync(string worktreePath, string baseBranch = "main", CancellationToken token = default)
        {
            if (ShouldThrowOnDiff) throw new InvalidOperationException("Simulated live diff failure");
            LiveDiffCalls.Add(worktreePath);
            return DiffReader == null ? Task.FromResult(DiffResult) : DiffReader.DiffWorkingTreeAsync(worktreePath, baseBranch, token);
        }

        public Task<IReadOnlyList<string>> GetChangedFilesSinceAsync(string worktreePath, string startCommit, CancellationToken token = default)
            => Task.FromResult(ChangedFilesSinceResult);

        /// <summary>When true, ReadChangedPathsAgainstBaseAsync throws, as an unreadable change does.</summary>
        public bool ShouldThrowOnReadChangedPaths { get; set; } = false;

        /// <summary>Paths returned by ReadChangedPathsAgainstBaseAsync; null derives them from DiffResult.</summary>
        public IReadOnlyList<string>? ChangedPathsResult { get; set; } = null;

        public Task<IReadOnlyList<string>> ReadChangedPathsAgainstBaseAsync(string worktreePath, string baseBranch = "main", CancellationToken token = default)
        {
            if (ShouldThrowOnReadChangedPaths) throw new InvalidOperationException("Simulated changed-path read failure");
            return Task.FromResult(ChangedPathsResult ?? Armada.Core.Services.GitDiffPaths.ExtractPaths(DiffResult));
        }

        /// <summary>
        /// When set, ReadChangedPathStatusesBetweenCommitsAsync is answered by this function, normally a real git
        /// service's read over a fixture repository. When null, the read is unsupported, as for any seam without a
        /// repository.
        /// </summary>
        public Func<string, string, string, int, TimeSpan, int, CancellationToken, Task<Armada.Core.Models.ChangedPathStatusRead>>? CommitPairRead { get; set; } = null;

        public Task<Armada.Core.Models.ChangedPathStatusRead> ReadChangedPathStatusesBetweenCommitsAsync(string repoPath, string fromCommit, string toCommit,
            int maxPaths, TimeSpan timeout, int outputLimitBytes, CancellationToken token = default)
        {
            if (CommitPairRead != null)
                return CommitPairRead(repoPath, fromCommit, toCommit, maxPaths, timeout, outputLimitBytes, token);
            return Task.FromResult(Armada.Core.Models.ChangedPathStatusRead.Unavailable(
                Armada.Core.Models.ChangedPathStatusRead.NotSupportedCode, "The stub git service reads no repository."));
        }

        /// <summary>Producer changed paths returned by GetChangedFilePathsAgainstBaseAsync.</summary>
        public IReadOnlyList<string> ChangedFilePathsAgainstBaseResult { get; set; } = Array.Empty<string>();

        /// <summary>When set, GetChangedFilePathsAgainstBaseAsync reports the read as unavailable with this reason.</summary>
        public string? ChangedFilePathsAgainstBaseFailure { get; set; } = null;

        /// <summary>When true, GetChangedFilePathsAgainstBaseAsync throws, as a seam that fails outright.</summary>
        public bool ShouldThrowOnChangedFilePathsAgainstBase { get; set; } = false;

        public Task<Armada.Core.Models.ChangedPathsRead> GetChangedFilePathsAgainstBaseAsync(string worktreePath, string baseBranch = "main", CancellationToken token = default)
        {
            if (ShouldThrowOnChangedFilePathsAgainstBase) throw new InvalidOperationException("Simulated producer changed-path read failure");
            if (ChangedFilePathsAgainstBaseFailure != null)
                return Task.FromResult(Armada.Core.Models.ChangedPathsRead.Unavailable(ChangedFilePathsAgainstBaseFailure));
            return Task.FromResult(Armada.Core.Models.ChangedPathsRead.FromPaths(ChangedFilePathsAgainstBaseResult));
        }

        public IReadOnlyList<string> ConflictedFilesResult { get; set; } = Array.Empty<string>();
        public Task<IReadOnlyList<string>> GetConflictedFilesAsync(string worktreePath, CancellationToken token = default)
            => Task.FromResult(ConflictedFilesResult);

        public Task<bool> IsPathTrackedAsync(string worktreePath, string relativePath, CancellationToken token = default)
            => Task.FromResult(TrackedPaths.Contains(relativePath));

        public Task<bool> IsPrMergedAsync(string workingDirectory, string prUrl, CancellationToken token = default) => Task.FromResult(IsPrMergedResult);
        public string? HeadCommitHashResult { get; set; } = "abc123def456";
        public Task<string?> GetHeadCommitHashAsync(string worktreePath, CancellationToken token = default) => Task.FromResult<string?>(HeadCommitHashResult);
        public Task<bool> BranchExistsAsync(string repoPath, string branchName, CancellationToken token = default)
        {
            if (ExistingBranches.Contains(branchName)) return Task.FromResult(true);
            if (branchName == "main") return Task.FromResult(DefaultBranchExistsResult);
            return Task.FromResult(false);
        }
        public Task<bool> EnsureLocalBranchAsync(string repoPath, string branchName, CancellationToken token = default)
            => BranchExistsAsync(repoPath, branchName, token);
        public Task<bool> IsWorktreeRegisteredAsync(string repoPath, string worktreePath, CancellationToken token = default) => Task.FromResult(false);

        public Task SetHeadSymbolicRefAsync(string repoPath, string targetRef, CancellationToken token = default)
        {
            if (ShouldThrowOnSetHeadSymbolicRef) throw new InvalidOperationException("Simulated HEAD symbolic-ref restore failure");
            OperationCalls.Add("set-head-symbolic-ref:" + targetRef);
            return Task.CompletedTask;
        }

        public Task<int> GetCommitCountBetweenAsync(string repoPath, string fromRef, string toRef, CancellationToken token = default)
            => Task.FromResult(0);

        /// <summary>
        /// Answers for TryCountCommitsBetweenAsync, keyed by "fromRef..toRef" as git rev-list reads the range. A range
        /// with no entry answers null, matching the interface default: git could not answer.
        /// </summary>
        public Dictionary<string, int> CommitCountsBetween { get; } = new Dictionary<string, int>(StringComparer.Ordinal);

        public Task<int?> TryCountCommitsBetweenAsync(string repoPath, string fromRef, string toRef, CancellationToken token = default)
        {
            return Task.FromResult(CommitCountsBetween.TryGetValue(fromRef + ".." + toRef, out int count) ? count : (int?)null);
        }

        /// <summary>Repository-relative paths that exist on a revision, keyed by the path alone.</summary>
        public HashSet<string> PathsOnRevision { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Recorded repository, revision and path for each tracked-path lookup.</summary>
        public List<string> PathExistsOnRevisionCalls { get; } = new List<string>();

        /// <summary>Repository-relative paths that exist on one revision only, keyed by <c>revision|path</c>.</summary>
        public HashSet<string> PathsAtRevision { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public Task<bool> PathExistsOnRevisionAsync(string worktreePath, string revision, string relativePath, CancellationToken token = default)
        {
            PathExistsOnRevisionCalls.Add(worktreePath + "|" + revision + "|" + relativePath);
            return Task.FromResult(PathsOnRevision.Contains(relativePath) || PathsAtRevision.Contains(revision + "|" + relativePath));
        }

        /// <summary>Tracked paths ResolveTrackedPathSuffixAsync returns, keyed by the path as named.</summary>
        public Dictionary<string, string> TrackedPathSuffixes { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>
        /// Tracked paths ResolveTrackedPathSuffixAsync returns for one repository only, keyed by
        /// <c>repository|path as named</c>; consulted before <see cref="TrackedPathSuffixes"/>.
        /// </summary>
        public Dictionary<string, string> TrackedPathSuffixesByRepository { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

        public Task<string?> ResolveTrackedPathSuffixAsync(string worktreePath, string revision, string relativePath, CancellationToken token = default)
        {
            if (TrackedPathSuffixesByRepository.TryGetValue(worktreePath + "|" + relativePath, out string? scoped)) return Task.FromResult<string?>(scoped);
            return Task.FromResult<string?>(TrackedPathSuffixes.TryGetValue(relativePath, out string? tracked) ? tracked : null);
        }

        /// <summary>Tracked files ListTrackedPathCandidatesAsync returns, keyed by the path as named.</summary>
        public Dictionary<string, List<string>> TrackedPathCandidates { get; } = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        /// <summary>
        /// Tracked files ListTrackedPathCandidatesAsync returns for one repository only, keyed by
        /// <c>repository|path as named</c>; consulted before <see cref="TrackedPathCandidates"/>.
        /// </summary>
        public Dictionary<string, List<string>> TrackedPathCandidatesByRepository { get; } = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        public Task<List<string>> ListTrackedPathCandidatesAsync(string worktreePath, string revision, string relativePath, CancellationToken token = default)
        {
            if (TrackedPathCandidatesByRepository.TryGetValue(worktreePath + "|" + relativePath, out List<string>? scoped))
                return Task.FromResult(new List<string>(scoped));
            return Task.FromResult(TrackedPathCandidates.TryGetValue(relativePath, out List<string>? candidates)
                ? new List<string>(candidates)
                : new List<string>());
        }

        /// <summary>File lines ReadFileLinesOnRevisionAsync returns, keyed by repository-relative path.</summary>
        public Dictionary<string, List<string>> FileLinesOnRevision { get; } = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        /// <summary>File lines on one revision only, keyed by <c>revision|path</c>; consulted before <see cref="FileLinesOnRevision"/>.</summary>
        public Dictionary<string, List<string>> FileLinesAtRevision { get; } = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        public Task<IReadOnlyList<string>?> ReadFileLinesOnRevisionAsync(string worktreePath, string revision, string relativePath, CancellationToken token = default)
        {
            if (FileLinesAtRevision.TryGetValue(revision + "|" + relativePath, out List<string>? atRevision)) return Task.FromResult<IReadOnlyList<string>?>(atRevision);
            return Task.FromResult<IReadOnlyList<string>?>(FileLinesOnRevision.TryGetValue(relativePath, out List<string>? lines) ? lines : null);
        }

        /// <summary>Terms reported as found by SearchTrackedContentOnRevisionAsync.</summary>
        public HashSet<string> FoundTermsOnRevision { get; } = new HashSet<string>(StringComparer.Ordinal);

        public Task<Armada.Core.Models.GitAnchorPriorArt> SearchTrackedContentOnRevisionAsync(
            string worktreePath, string revision, string term, int maxSamples, CancellationToken token = default)
        {
            Armada.Core.Models.GitAnchorPriorArt result = new Armada.Core.Models.GitAnchorPriorArt { Term = term ?? "" };
            result.Found = FoundTermsOnRevision.Contains(term ?? "");
            return Task.FromResult(result);
        }
    }
}
