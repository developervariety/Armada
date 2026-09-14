namespace Armada.Core.Services.Interfaces
{
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;

    /// <summary>
    /// Read-only branch inspection used by reporting callers.
    /// </summary>
    /// <remarks>
    /// Deliberately separate from <see cref="IGitService"/>. These operations are only needed by
    /// reporting code, and folding them into the main git interface would force all seventeen
    /// existing test doubles to grow members they never exercise -- churn that buys nothing and
    /// invites stubs that silently return the wrong answer.
    /// </remarks>
    public interface IBranchInventory
    {
        /// <summary>
        /// List local branches with tip metadata and divergence from the default branch.
        /// This operation must not fetch or modify repository refs.
        /// </summary>
        /// <param name="repoPath">Repository path.</param>
        /// <param name="defaultBranch">Configured default branch.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Branches, with the default branch first.</returns>
        Task<IReadOnlyList<BranchInfo>> ListBranchesAsync(string repoPath, string defaultBranch = "main", CancellationToken token = default);

        /// <summary>Reads the repository symbolic HEAD ref without changing it.</summary>
        /// <param name="repoPath">Repository path.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The symbolic HEAD ref.</returns>
        Task<string> GetRepositoryHeadRefAsync(string repoPath, CancellationToken token = default);

        /// <summary>Inspects HEAD and verifies detached state without changing the repository.</summary>
        /// <param name="repoPath">Repository path.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Verified symbolic or detached HEAD state.</returns>
        Task<RepositoryHeadInspection> InspectRepositoryHeadAsync(string repoPath, CancellationToken token = default);

        /// <summary>Determines whether the repository is bare without changing it.</summary>
        /// <param name="repoPath">Repository path.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>True when the repository has no working tree.</returns>
        Task<bool> IsBareRepositoryAsync(string repoPath, CancellationToken token = default);

        /// <summary>
        /// List local branch names in the repository, optionally restricted to those starting with
        /// a prefix. Returns an empty list when the repository cannot be read.
        /// </summary>
        /// <param name="repoPath">Path to the repository.</param>
        /// <param name="branchPrefix">Optional branch-name prefix filter, for example "armada/".</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Local branch short names.</returns>
        Task<IReadOnlyList<string>> EnumerateLocalBranchesAsync(string repoPath, string? branchPrefix = null, CancellationToken token = default);

        /// <summary>
        /// Determine whether one commit-ish is an ancestor of another, i.e. whether
        /// <paramref name="ancestorRef"/> has already been merged into <paramref name="descendantRef"/>.
        /// Returns false when either ref is missing or the comparison cannot be made.
        /// </summary>
        /// <param name="repoPath">Path to the repository.</param>
        /// <param name="ancestorRef">Candidate ancestor ref.</param>
        /// <param name="descendantRef">Candidate descendant ref.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>True when ancestorRef is an ancestor of descendantRef.</returns>
        Task<bool> IsAncestorAsync(string repoPath, string ancestorRef, string descendantRef, CancellationToken token = default);

        /// <summary>
        /// List the refs under a prefix with their tips and committer times. Unlike
        /// <see cref="EnumerateLocalBranchesAsync"/>, an unreadable repository throws.
        /// </summary>
        /// <param name="repoPath">Path to the repository.</param>
        /// <param name="refPrefix">Fully qualified ref prefix, for example refs/armada-preserved/.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Ref tips.</returns>
        Task<IReadOnlyList<GitRefTip>> EnumerateRefTipsAsync(string repoPath, string refPrefix, CancellationToken token = default);

        /// <summary>
        /// List every ref a remote advertises, with its tip. Commit times are not available from a
        /// remote listing. A remote that cannot be reached throws.
        /// </summary>
        /// <param name="repoPath">Repository whose remote is listed.</param>
        /// <param name="remoteName">Remote name.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Remote ref tips.</returns>
        Task<IReadOnlyList<GitRefTip>> EnumerateRemoteRefTipsAsync(string repoPath, string remoteName = "origin", CancellationToken token = default);

        /// <summary>
        /// Read a commit's committer time, or null when the repository does not hold the commit.
        /// </summary>
        /// <param name="repoPath">Path to the repository.</param>
        /// <param name="commitSha">Commit SHA.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Committer time in UTC, or null.</returns>
        Task<DateTime?> TryGetCommitTimeUtcAsync(string repoPath, string commitSha, CancellationToken token = default);

        /// <summary>
        /// Delete a ref only while it still points at the expected commit.
        /// Executes: git update-ref -d {refName} {expectedSha}
        /// </summary>
        /// <param name="repoPath">Path to the repository.</param>
        /// <param name="refName">Fully qualified ref name.</param>
        /// <param name="expectedSha">Commit the ref must still point at.</param>
        /// <param name="token">Cancellation token.</param>
        Task DeleteRefIfAtAsync(string repoPath, string refName, string expectedSha, CancellationToken token = default);

        /// <summary>
        /// Delete a ref on a remote only while it still points at the expected commit.
        /// Executes: git push --force-with-lease={refName}:{expectedSha} {remoteName} :{refName}
        /// </summary>
        /// <param name="repoPath">Repository whose remote is changed.</param>
        /// <param name="remoteName">Remote name.</param>
        /// <param name="refName">Fully qualified ref name on the remote.</param>
        /// <param name="expectedSha">Commit the remote ref must still point at.</param>
        /// <param name="token">Cancellation token.</param>
        Task DeleteRemoteRefIfAtAsync(string repoPath, string remoteName, string refName, string expectedSha, CancellationToken token = default);
    }
}
