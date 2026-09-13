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
    }
}
