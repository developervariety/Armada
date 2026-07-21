namespace Armada.Core.Services.Interfaces
{
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;

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
