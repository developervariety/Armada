namespace Armada.Core.Services.Interfaces
{
    using System.Collections.Generic;
    using Armada.Core.Models;

    /// <summary>
    /// Git operations for repository and worktree management.
    /// </summary>
    public interface IGitService
    {
        /// <summary>
        /// Clone a repository as a bare repo.
        /// </summary>
        /// <param name="repoUrl">Remote repository URL.</param>
        /// <param name="localPath">Local path for the bare clone.</param>
        /// <param name="token">Cancellation token.</param>
        Task CloneBareAsync(string repoUrl, string localPath, CancellationToken token = default);

        /// <summary>
        /// Create a git worktree from a bare repository.
        /// </summary>
        /// <param name="repoPath">Path to the bare repository.</param>
        /// <param name="worktreePath">Path for the new worktree.</param>
        /// <param name="branchName">Branch name to create and checkout.</param>
        /// <param name="baseBranch">Base branch to create from.</param>
        /// <param name="detached">When true, create the worktree in detached HEAD state using the existing ref rather than a named branch.</param>
        /// <param name="token">Cancellation token.</param>
        Task CreateWorktreeAsync(string repoPath, string worktreePath, string branchName, string baseBranch = "main", bool detached = false, CancellationToken token = default);

        /// <summary>
        /// Remove a git worktree.
        /// </summary>
        /// <param name="worktreePath">Path to the worktree to remove.</param>
        /// <param name="token">Cancellation token.</param>
        Task RemoveWorktreeAsync(string worktreePath, CancellationToken token = default);

        /// <summary>
        /// Fetch latest changes from remote.
        /// </summary>
        /// <param name="repoPath">Path to the repository.</param>
        /// <param name="token">Cancellation token.</param>
        Task FetchAsync(string repoPath, CancellationToken token = default);

        /// <summary>
        /// Push a branch to the remote.
        /// </summary>
        /// <param name="worktreePath">Path to the worktree.</param>
        /// <param name="remoteName">Remote name.</param>
        /// <param name="token">Cancellation token.</param>
        Task PushBranchAsync(string worktreePath, string remoteName = "origin", CancellationToken token = default);

        /// <summary>
        /// Create a pull request using the gh CLI.
        /// </summary>
        /// <param name="worktreePath">Path to the worktree.</param>
        /// <param name="title">PR title.</param>
        /// <param name="body">PR body.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>PR URL.</returns>
        Task<string> CreatePullRequestAsync(string worktreePath, string title, string body, CancellationToken token = default);

        /// <summary>
        /// Repair a worktree by resetting it to a clean state.
        /// </summary>
        /// <param name="worktreePath">Path to the worktree.</param>
        /// <param name="token">Cancellation token.</param>
        Task RepairWorktreeAsync(string worktreePath, CancellationToken token = default);

        /// <summary>
        /// Check if a path is a valid git repository.
        /// </summary>
        /// <param name="path">Path to check.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>True if the path is a git repository.</returns>
        Task<bool> IsRepositoryAsync(string path, CancellationToken token = default);

        /// <summary>
        /// Delete a local branch from a repository.
        /// </summary>
        /// <param name="repoPath">Path to the repository.</param>
        /// <param name="branchName">Branch name to delete.</param>
        /// <param name="token">Cancellation token.</param>
        Task DeleteLocalBranchAsync(string repoPath, string branchName, CancellationToken token = default, [System.Runtime.CompilerServices.CallerMemberName] string caller = "");

        /// <summary>
        /// Delete a branch from the remote origin.
        /// Executes: git push origin --delete {branchName}
        /// </summary>
        /// <param name="repoPath">Path to a repository with the remote configured.</param>
        /// <param name="branchName">Remote branch name to delete.</param>
        /// <param name="token">Cancellation token.</param>
        Task DeleteRemoteBranchAsync(string repoPath, string branchName, CancellationToken token = default, [System.Runtime.CompilerServices.CallerMemberName] string caller = "");

        /// <summary>
        /// Push a specific source ref to a destination ref on origin.
        /// Executes: git push origin {srcRef}:{destRef}
        /// </summary>
        /// <param name="repoPath">Path to the repository (bare or worktree).</param>
        /// <param name="srcRef">Source ref (branch or commit) to push.</param>
        /// <param name="destRef">Destination ref on origin to update.</param>
        /// <param name="token">Cancellation token.</param>
        Task PushRefSpecAsync(string repoPath, string srcRef, string destRef, CancellationToken token = default);

        /// <summary>
        /// Force-update a local branch ref to a specific commit without moving the worktree.
        /// Executes: git update-ref refs/heads/{branchName} {commitSha}
        /// </summary>
        /// <param name="repoPath">Path to the repository (bare or worktree).</param>
        /// <param name="branchName">Branch name whose ref is force-updated.</param>
        /// <param name="commitSha">Commit SHA the branch ref is pointed at.</param>
        /// <param name="token">Cancellation token.</param>
        Task ForceUpdateBranchRefAsync(string repoPath, string branchName, string commitSha, CancellationToken token = default)
        {
            // Optional stage-lag hardening. A stub that does not exercise the ref move still
            // satisfies the contract; the real GitService performs the update-ref.
            return Task.CompletedTask;
        }

        /// <summary>
        /// Create a worktree with a detached HEAD at an exact commit.
        /// Executes: git worktree add --detach {worktreePath} {commitSha}
        /// </summary>
        /// <remarks>
        /// A detached worktree binds no branch, so git never refuses it because another worktree
        /// already has that branch checked out. The default implementation delegates to the detached
        /// form of <see cref="CreateWorktreeAsync"/> so existing test doubles keep recording the call.
        /// </remarks>
        /// <param name="repoPath">Path to the repository (bare or worktree).</param>
        /// <param name="worktreePath">Path of the worktree to create.</param>
        /// <param name="commitSha">Commit the detached HEAD is placed at.</param>
        /// <param name="token">Cancellation token.</param>
        Task CreateDetachedWorktreeAtCommitAsync(string repoPath, string worktreePath, string commitSha, CancellationToken token = default)
        {
            return CreateWorktreeAsync(repoPath, worktreePath, commitSha, commitSha, true, token);
        }

        /// <summary>
        /// Move a local branch ref to a new commit only when it still points at the expected commit.
        /// Executes: git update-ref refs/heads/{branchName} {newSha} {expectedOldSha}
        /// </summary>
        /// <remarks>
        /// Git refuses the update when the ref has moved since it was read, so a concurrent writer is
        /// reported instead of overwritten. The default implementation is a no-op for test doubles that
        /// do not exercise ref moves.
        /// </remarks>
        /// <param name="repoPath">Path to the repository (bare or worktree).</param>
        /// <param name="branchName">Branch whose ref is advanced.</param>
        /// <param name="newSha">Commit the ref is moved to.</param>
        /// <param name="expectedOldSha">Commit the ref must still point at.</param>
        /// <param name="token">Cancellation token.</param>
        Task CompareAndSwapBranchRefAsync(string repoPath, string branchName, string newSha, string expectedOldSha, CancellationToken token = default)
        {
            return Task.CompletedTask;
        }

        /// <summary>
        /// Push the worktree's HEAD to a named branch on a remote. Works from a detached HEAD.
        /// Executes: git push {remoteName} HEAD:refs/heads/{branchName}
        /// </summary>
        /// <remarks>
        /// The default implementation delegates to <see cref="PushBranchAsync"/> so existing test
        /// doubles keep recording the push.
        /// </remarks>
        /// <param name="worktreePath">Worktree whose HEAD is pushed.</param>
        /// <param name="remoteName">Remote name.</param>
        /// <param name="branchName">Destination branch on the remote.</param>
        /// <param name="token">Cancellation token.</param>
        Task PushHeadToBranchAsync(string worktreePath, string remoteName, string branchName, CancellationToken token = default)
        {
            return PushBranchAsync(worktreePath, remoteName, token);
        }

        /// <summary>
        /// Copy a ref to another ref name inside the same repository, without network access.
        /// Executes: git update-ref {destRef} {srcRef}
        /// </summary>
        /// <remarks>
        /// Used to park a branch under refs/armada-preserved/ before the branch itself is deleted, so
        /// the commit stays reachable by name instead of surviving only as a dangling object that can
        /// be recovered only if someone recorded its SHA. Default-implemented as a no-op so existing
        /// test doubles need not grow a member they do not exercise.
        /// </remarks>
        /// <param name="repoPath">Path to the repository (bare or worktree).</param>
        /// <param name="srcRef">Source ref to copy, for example refs/heads/armada/captain/msn_x.</param>
        /// <param name="destRef">Fully-qualified destination ref.</param>
        /// <param name="token">Cancellation token.</param>
        Task CopyRefAsync(string repoPath, string srcRef, string destRef, CancellationToken token = default)
        {
            return Task.CompletedTask;
        }

        /// <summary>
        /// Get the symbolic ref currently stored in repository HEAD.
        /// </summary>
        /// <param name="repoPath">Path to the repository (bare or worktree).</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The symbolic HEAD ref, such as refs/heads/main.</returns>
        Task<string> GetRepositoryHeadRefAsync(string repoPath, CancellationToken token = default);

        /// <summary>
        /// Set repository HEAD to the symbolic ref for a local branch.
        /// </summary>
        /// <param name="repoPath">Path to the repository (bare or worktree).</param>
        /// <param name="branchName">Local branch name to store in HEAD.</param>
        /// <param name="token">Cancellation token.</param>
        Task SetRepositoryHeadAsync(string repoPath, string branchName, CancellationToken token = default);

        /// <summary>
        /// Prune stale worktree registrations (entries for worktrees whose directories no longer exist).
        /// </summary>
        /// <param name="repoPath">Path to the repository.</param>
        /// <param name="token">Cancellation token.</param>
        Task PruneWorktreesAsync(string repoPath, CancellationToken token = default);

        /// <summary>
        /// Enable auto-merge on a pull request using the gh CLI.
        /// </summary>
        /// <param name="worktreePath">Path to the worktree (for gh context).</param>
        /// <param name="prUrl">PR URL to auto-merge.</param>
        /// <param name="token">Cancellation token.</param>
        Task EnableAutoMergeAsync(string worktreePath, string prUrl, CancellationToken token = default);

        /// <summary>
        /// Merge a branch from a source repository into the current branch of a target working directory.
        /// Fetches the branch from sourceRepoPath and merges it into targetWorkDir.
        /// </summary>
        /// <param name="targetWorkDir">The user's local working directory.</param>
        /// <param name="sourceRepoPath">Path to the bare repo containing the branch.</param>
        /// <param name="branchName">Branch name to fetch and merge.</param>
        /// <param name="targetBranch">Target branch to checkout before merging (e.g. "main", "develop"). If null, uses current branch.</param>
        /// <param name="commitMessage">Optional custom merge commit message. If null, uses default.</param>
        /// <param name="token">Cancellation token.</param>
        Task MergeBranchLocalAsync(string targetWorkDir, string sourceRepoPath, string branchName, string? targetBranch = null, string? commitMessage = null, CancellationToken token = default);

        /// <summary>
        /// Pull latest changes from remote into a working directory.
        /// </summary>
        /// <param name="workingDirectory">Path to the working directory.</param>
        /// <param name="token">Cancellation token.</param>
        Task PullAsync(string workingDirectory, CancellationToken token = default);

        /// <summary>
        /// Pull latest changes from remote into a working directory, failing unless the update is fast-forward only.
        /// </summary>
        /// <param name="workingDirectory">Path to the working directory.</param>
        /// <param name="token">Cancellation token.</param>
        Task PullFastForwardOnlyAsync(string workingDirectory, CancellationToken token = default);

        /// <summary>
        /// Get the current branch name for a working directory.
        /// </summary>
        /// <param name="workingDirectory">Path to the working directory.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The current branch name, or null if it cannot be determined.</returns>
        Task<string?> GetCurrentBranchAsync(string workingDirectory, CancellationToken token = default);

        /// <summary>
        /// Check whether a working directory has no tracked or untracked changes.
        /// </summary>
        /// <param name="workingDirectory">Path to the working directory.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>True if the working directory is clean.</returns>
        Task<bool> IsWorkingDirectoryCleanAsync(string workingDirectory, CancellationToken token = default);

        /// <summary>
        /// Check whether the working directory has uncommitted changes to TRACKED files only.
        /// Untracked files are ignored, because a fast-forward preserves them and they never block
        /// a landing sync (git status --porcelain --untracked-files=no). Use this to guard an
        /// operation that itself tolerates untracked files, so the guard is not stricter than it.
        /// </summary>
        /// <param name="workingDirectory">Path to the working directory.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>True if tracked files have uncommitted changes.</returns>
        Task<bool> HasUncommittedTrackedChangesAsync(string workingDirectory, CancellationToken token = default);

        /// <summary>
        /// Get the diff of all changes in a worktree against the base branch.
        /// </summary>
        /// <param name="worktreePath">Path to the worktree.</param>
        /// <param name="baseBranch">Base branch to diff against.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Unified diff output.</returns>
        Task<string> DiffAsync(string worktreePath, string baseBranch = "main", CancellationToken token = default);

        /// <summary>
        /// Get the HEAD commit hash of a worktree.
        /// </summary>
        /// <param name="worktreePath">Path to the worktree.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The full SHA-1 commit hash, or null if it cannot be determined.</returns>
        Task<string?> GetHeadCommitHashAsync(string worktreePath, CancellationToken token = default);

        /// <summary>
        /// Get the list of files with unresolved merge conflicts (unmerged paths) in a worktree.
        /// Returns an empty list when the working tree is clean.
        /// </summary>
        /// <param name="worktreePath">Path to the worktree.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Repository-relative paths of conflicted files.</returns>
        Task<IReadOnlyList<string>> GetConflictedFilesAsync(string worktreePath, CancellationToken token = default);

        // Mission-brief anchor queries. These enrich a brief; they are never required for a dispatch
        // to proceed, so each carries a default that reports "nothing resolved". An implementation
        // that cannot answer them degrades to a brief without anchors, which the renderer states
        // explicitly rather than passing off as an empty result.

        /// <summary>
        /// Resolve a revision to its abbreviated commit hash.
        /// </summary>
        /// <param name="repoPath">Repository or worktree path.</param>
        /// <param name="revision">Revision to resolve, for example a branch name.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The abbreviated hash, or null when it cannot be resolved.</returns>
        Task<string?> GetRevisionShaAsync(string repoPath, string revision, CancellationToken token = default)
        {
            return Task.FromResult<string?>(null);
        }

        /// <summary>
        /// Resolve a revision to the full object ID of a commit that exists in this repository.
        /// </summary>
        /// <remarks>
        /// Use this for branch creation and other writes. A hexadecimal string can be parsed as a
        /// revision name even when the named object is absent, so write paths must verify the commit
        /// object and must not replace it with an abbreviated display value.
        /// </remarks>
        Task<string?> GetRevisionCommitShaAsync(string repoPath, string revision, CancellationToken token = default)
        {
            return Task.FromResult<string?>(null);
        }

        /// <summary>
        /// Resolve the committer time of the commit a revision names.
        /// </summary>
        /// <remarks>
        /// Stall evaluation reads it as evidence of work: a branch tip committed inside the stall
        /// window means the captain is working. An implementation that cannot answer returns null,
        /// which the evaluator reports as "branch tip unavailable" rather than as an old commit.
        /// </remarks>
        /// <param name="repoPath">Repository or worktree path.</param>
        /// <param name="revision">Revision to resolve, for example a branch name.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The UTC committer time, or null when it cannot be resolved.</returns>
        Task<DateTime?> GetCommitTimeUtcAsync(string repoPath, string revision, CancellationToken token = default)
        {
            return Task.FromResult<DateTime?>(null);
        }

        /// <summary>Resolve an exact or unique suffix path at an immutable revision. Query errors propagate.</summary>
        Task<string?> ResolveAnchorPathOnRevisionAsync(string worktreePath, string revision, string relativePath,
            CancellationToken token = default) => throw new NotSupportedException("Pinned path queries are not supported.");

        /// <summary>
        /// Read up to five path-history entries from an explicit revision, using full commit IDs.
        /// Throws when the revision or query is unavailable; an empty result means verified empty history.
        /// </summary>
        Task<IReadOnlyList<GitAnchorCommit>> GetCommitsTouchingPathOnRevisionAsync(string worktreePath,
            string revision, string relativePath, int maxCount, CancellationToken token = default)
        {
            throw new NotSupportedException("Pinned path history is unavailable.");
        }

        /// <summary>
        /// Search tracked content on an explicit revision with up to three repository-relative samples.
        /// Only a successful no-match exit is absence; errors and cancellation propagate.
        /// </summary>
        Task<GitAnchorPriorArt> SearchTrackedContentOnRevisionAsync(string worktreePath,
            string revision, string term, int maxSamples, CancellationToken token = default)
        {
            throw new NotSupportedException("Pinned content search is unavailable.");
        }

        /// <summary>
        /// Read a bounded window of a tracked file as it exists on a revision
        /// (<c>git show &lt;commit&gt;:&lt;path&gt;</c>), centred near a 1-based line. The window never
        /// exceeds <paramref name="maxLines"/> lines, and credential-shaped text is redacted.
        /// </summary>
        /// <param name="worktreePath">Path to the repository or worktree.</param>
        /// <param name="revision">Branch, ref, or commit to read.</param>
        /// <param name="relativePath">Repository-relative path.</param>
        /// <param name="line">The 1-based line the window is placed around.</param>
        /// <param name="maxLines">Maximum lines returned.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The excerpt, or null when the file cannot be read on that revision.</returns>
        Task<string?> ReadFileExcerptOnRevisionAsync(string worktreePath,
            string revision, string relativePath, int line, int maxLines, CancellationToken token = default)
        {
            return Task.FromResult<string?>(null);
        }

        /// <summary>
        /// Whether one ref is an ancestor of another, or null when it cannot be determined.
        /// </summary>
        /// <remarks>
        /// The default is null - UNKNOWN - and deliberately not true. An implementation that does
        /// not really consult a repository must not be able to answer "yes, the base is correct";
        /// a caller acting on that would report a verification it never performed.
        /// </remarks>
        /// <param name="repoPath">Repository or worktree path.</param>
        /// <param name="ancestorRef">The ref that should be contained.</param>
        /// <param name="descendantRef">The ref that should contain it.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>True, false, or null when ancestry could not be established.</returns>
        Task<bool?> TryIsAncestorAsync(string repoPath, string ancestorRef, string descendantRef, CancellationToken token = default)
        {
            return Task.FromResult<bool?>(null);
        }

        /// <summary>
        /// List the most recent commits that touched a path, newest first.
        /// </summary>
        /// <param name="worktreePath">Path to the worktree.</param>
        /// <param name="relativePath">Repository-relative path.</param>
        /// <param name="maxCount">Maximum commits to return; zero or less returns none.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Commits touching the path, newest first; empty when there are none.</returns>
        Task<IReadOnlyList<GitAnchorCommit>> GetCommitsTouchingPathAsync(
            string worktreePath,
            string relativePath,
            int maxCount,
            CancellationToken token = default)
        {
            return Task.FromResult<IReadOnlyList<GitAnchorCommit>>(Array.Empty<GitAnchorCommit>());
        }

        /// <summary>
        /// Report whether a path exists on a revision.
        /// </summary>
        /// <param name="worktreePath">Path to the worktree.</param>
        /// <param name="revision">Revision to test; defaults to HEAD when empty.</param>
        /// <param name="relativePath">Repository-relative path.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>True when the path exists on the revision.</returns>
        Task<bool> PathExistsOnRevisionAsync(
            string worktreePath,
            string revision,
            string relativePath,
            CancellationToken token = default)
        {
            return Task.FromResult(false);
        }

        /// <summary>
        /// Resolve a path a mission named by a suffix of its tracked path to the single tracked path
        /// that ends with it. A mission commonly names a file the way a reader would say it aloud
        /// ("Decoders/Foo.cs") rather than from the repository root, and an exact-path test on
        /// that name answers "absent" about a file that is present.
        /// </summary>
        /// <param name="worktreePath">Path to the worktree.</param>
        /// <param name="revision">Revision to resolve against; defaults to HEAD when empty.</param>
        /// <param name="relativePath">Path as the mission named it.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The tracked path when exactly one matches; null when none or several match.</returns>
        Task<string?> ResolveTrackedPathSuffixAsync(
            string worktreePath,
            string revision,
            string relativePath,
            CancellationToken token = default)
        {
            return Task.FromResult<string?>(null);
        }

        /// <summary>
        /// Search tracked content for a fixed term and report how many files contain it, with a few
        /// sample locations. A caller must establish that the repository answers git before calling
        /// this, because a search that cannot run is reported the same way as one that found nothing.
        /// </summary>
        /// <param name="worktreePath">Path to the worktree.</param>
        /// <param name="term">Fixed string to search for.</param>
        /// <param name="maxSamples">Maximum sample locations to collect.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The search result; never null.</returns>
        Task<GitAnchorPriorArt> SearchTrackedContentAsync(
            string worktreePath,
            string term,
            int maxSamples,
            CancellationToken token = default)
        {
            GitAnchorPriorArt empty = new GitAnchorPriorArt();
            empty.Term = term ?? "";
            return Task.FromResult(empty);
        }

        /// <summary>
        /// List repository-relative files changed during a mission since the worktree was provisioned.
        /// Includes committed changes plus current tracked and untracked working tree changes.
        /// </summary>
        /// <param name="worktreePath">Path to the worktree.</param>
        /// <param name="startCommit">Commit hash recorded when the dock was provisioned.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Normalized changed file paths.</returns>
        Task<IReadOnlyList<string>> GetChangedFilesSinceAsync(string worktreePath, string startCommit, CancellationToken token = default);

        /// <summary>
        /// List repository-relative paths that differ between a base branch and the worktree tip
        /// (base...HEAD). Used by the definition-of-done gate to decide whether a producer change
        /// can break a consumer, so the consumer's suite runs only on changes that reach a
        /// triggering path.
        /// </summary>
        /// <remarks>
        /// The default returns an empty list, so a git seam that does not implement this member
        /// never triggers a consumer-test run. An implementation that cannot answer returns an
        /// empty list rather than throwing, which the caller treats as "no triggering change".
        /// </remarks>
        /// <param name="worktreePath">Path to the worktree.</param>
        /// <param name="baseBranch">Base branch to diff against.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Normalized changed file paths; empty when none or when unavailable.</returns>
        Task<IReadOnlyList<string>> GetChangedFilePathsAgainstBaseAsync(string worktreePath, string baseBranch = "main", CancellationToken token = default)
        {
            return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        }

        /// <summary>
        /// Check if a pull request has been merged using the gh CLI.
        /// </summary>
        /// <param name="workingDirectory">Path to a repo for gh context.</param>
        /// <param name="prUrl">PR URL to check.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>True if the PR has been merged.</returns>
        Task<bool> IsPrMergedAsync(string workingDirectory, string prUrl, CancellationToken token = default);

        /// <summary>
        /// Whether a repo-relative path is tracked by git (committed to the index). Used to decide
        /// where the generated mission instructions may live: a tracked root instruction file such
        /// as CLAUDE.md must never be overwritten, so the generated file goes under
        /// .armada/instructions/ instead. A root file that is merely present but untracked was
        /// written by an earlier generation pass and is the canonical location.
        /// </summary>
        /// <param name="worktreePath">Path to the worktree.</param>
        /// <param name="relativePath">Repo-relative path to test, e.g. "AGENTS.md".</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>True when the path is tracked by git.</returns>
        Task<bool> IsPathTrackedAsync(string worktreePath, string relativePath, CancellationToken token = default)
        {
            // Default: untracked. Real GitService overrides; unit stubs that predate the member
            // keep the behavior they had before it existed (mission brief lands at the root).
            return Task.FromResult(false);
        }

        /// <summary>
        /// Check if a local branch exists in the repository.
        /// </summary>
        /// <param name="repoPath">Path to the repository.</param>
        /// <param name="branchName">Branch name to check.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>True if the branch exists.</returns>
        Task<bool> BranchExistsAsync(string repoPath, string branchName, CancellationToken token = default);

        /// <summary>
        /// Ensure a local branch exists in the repository.
        /// If the matching remote branch exists, sync from it. Otherwise create the branch
        /// from the repository's effective default branch or another available base ref.
        /// Returns false only when the repository has no usable branch history yet.
        /// </summary>
        /// <param name="repoPath">Path to the repository.</param>
        /// <param name="branchName">Branch name to ensure.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>True if the branch exists or was created; false if the repo has no commits/branches.</returns>
        Task<bool> EnsureLocalBranchAsync(string repoPath, string branchName, CancellationToken token = default);

        /// <summary>
        /// Check if a path is registered as a git worktree.
        /// </summary>
        /// <param name="repoPath">Path to the repository.</param>
        /// <param name="worktreePath">Path to check.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>True if the path is a registered worktree.</returns>
        Task<bool> IsWorktreeRegisteredAsync(string repoPath, string worktreePath, CancellationToken token = default);

        /// <summary>
        /// Set the HEAD symbolic-ref in a bare repository to point to the given branch ref.
        /// Used to restore a bare repo HEAD after captain or integration branch cleanup so
        /// subsequent git operations do not see a dangling HEAD.
        /// </summary>
        /// <param name="repoPath">Path to the bare repository.</param>
        /// <param name="targetRef">Full ref name to point HEAD at (e.g. refs/heads/main).</param>
        /// <param name="token">Cancellation token.</param>
        Task SetHeadSymbolicRefAsync(string repoPath, string targetRef, CancellationToken token = default);

        /// <summary>
        /// Count commits reachable from <paramref name="toRef"/> but not from <paramref name="fromRef"/>.
        /// </summary>
        /// <param name="repoPath">Path to the repository.</param>
        /// <param name="fromRef">Base ref.</param>
        /// <param name="toRef">Tip ref.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Number of commits ahead; 0 when refs are equal or on any error.</returns>
        Task<int> GetCommitCountBetweenAsync(string repoPath, string fromRef, string toRef, CancellationToken token = default);

        /// <summary>
        /// Count the commits reachable from <paramref name="toRef"/> and not from <paramref name="fromRef"/>.
        /// Executes: git rev-list --count {fromRef}..{toRef}
        /// </summary>
        /// <remarks>
        /// Unlike <see cref="GetCommitCountBetweenAsync"/>, a failure is null rather than zero, so a
        /// caller deciding whether unrecorded work exists can tell "none" from "could not tell". The
        /// default implementation answers null.
        /// </remarks>
        /// <param name="repoPath">Repository or checkout in which both refs resolve.</param>
        /// <param name="fromRef">Excluded ref.</param>
        /// <param name="toRef">Included ref.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The commit count, or null when git could not answer.</returns>
        Task<int?> TryCountCommitsBetweenAsync(string repoPath, string fromRef, string toRef, CancellationToken token = default)
        {
            return Task.FromResult<int?>(null);
        }

        /// <summary>
        /// Push the HEAD of a checkout to a branch in another local repository, without force.
        /// Executes: git push {repositoryPath} HEAD:refs/heads/{branchName}
        /// </summary>
        /// <remarks>
        /// The destination is a repository path, never a named remote, so this cannot reach a hosted
        /// remote by accident. The default implementation throws, so a double that cannot push says so.
        /// </remarks>
        /// <param name="worktreePath">Checkout whose HEAD is pushed.</param>
        /// <param name="repositoryPath">Local repository that receives the branch.</param>
        /// <param name="branchName">Destination branch name, without refs/heads/.</param>
        /// <param name="token">Cancellation token.</param>
        Task PushHeadToRepositoryBranchAsync(string worktreePath, string repositoryPath, string branchName, CancellationToken token = default)
        {
            throw new NotSupportedException("This git service cannot push a checkout HEAD to a repository path.");
        }
    }
}
