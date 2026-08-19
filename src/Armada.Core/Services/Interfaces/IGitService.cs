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
        Task DeleteLocalBranchAsync(string repoPath, string branchName, CancellationToken token = default);

        /// <summary>
        /// Delete a branch from the remote origin.
        /// Executes: git push origin --delete {branchName}
        /// </summary>
        /// <param name="repoPath">Path to a repository with the remote configured.</param>
        /// <param name="branchName">Remote branch name to delete.</param>
        /// <param name="token">Cancellation token.</param>
        Task DeleteRemoteBranchAsync(string repoPath, string branchName, CancellationToken token = default);

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
    }
}
