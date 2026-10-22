namespace Armada.Core.Models
{
    using System;

    /// <summary>
    /// Result of a merge-queue enqueue: the entry that will land the branch, and whether the request joined an
    /// entry the branch already had instead of creating one.
    /// </summary>
    public sealed class BranchEnqueueResult
    {
        #region Public-Members

        /// <summary>The created entry, or the branch's existing active entry.</summary>
        public MergeEntry Entry { get; }

        /// <summary>True when the request attached to an existing active entry for the same vessel, branch and target.</summary>
        public bool Attached { get; }

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="entry">Entry that lands the branch.</param>
        /// <param name="attached">Whether the request attached to an existing entry.</param>
        public BranchEnqueueResult(MergeEntry entry, bool attached)
        {
            Entry = entry ?? throw new ArgumentNullException(nameof(entry));
            Attached = attached;
        }

        #endregion
    }
}
