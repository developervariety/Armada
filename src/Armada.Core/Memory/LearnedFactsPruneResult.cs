namespace Armada.Core.Memory
{
    using System.Collections.Generic;

    /// <summary>
    /// Result of pruning the canonical per-vessel learned-facts file.
    /// </summary>
    public sealed class LearnedFactsPruneResult
    {
        #region Public-Members

        /// <summary>
        /// Gets or sets a value indicating whether the prune succeeded.
        /// </summary>
        public bool Success { get; set; }

        /// <summary>
        /// Gets or sets an error code or message when the prune failed.
        /// </summary>
        public string? Error { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the file content changed.
        /// </summary>
        public bool Changed { get; set; }

        /// <summary>
        /// Gets or sets the final content after pruning.
        /// </summary>
        public string? PrunedContent { get; set; }

        /// <summary>
        /// Gets or sets the number of entries removed to satisfy the cap.
        /// </summary>
        public int RemovedCount { get; set; }

        /// <summary>
        /// Gets or sets the number of entries merged as near-duplicates.
        /// </summary>
        public int MergedCount { get; set; }

        /// <summary>
        /// Gets or sets the text of entries that were removed.
        /// </summary>
        public List<string> RemovedEntries { get; set; } = new List<string>();

        /// <summary>
        /// Gets or sets human-readable descriptions of merge operations.
        /// </summary>
        public List<string> MergedEntries { get; set; } = new List<string>();

        #endregion
    }
}
