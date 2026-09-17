namespace Armada.Core.Models
{
    /// <summary>
    /// Whether a vessel's stale code index matters for dispatch: how many files changed between the
    /// indexed commit and the current default-branch commit, and how many of those are indexable
    /// source. A stale index whose only changed files are docs, excluded paths, or non-source is not
    /// relevant — dispatch can proceed without a refresh. This is the authoritative deterministic
    /// state a dispatch_staleness typed decision tie-breaks over; it never blocks anything itself.
    /// </summary>
    public class CodeIndexStalenessRelevance
    {
        #region Public-Members

        /// <summary>Vessel evaluated.</summary>
        public string VesselId { get; set; } = "";

        /// <summary>Commit the index was built from, or null when never indexed.</summary>
        public string? IndexedCommitSha { get; set; } = null;

        /// <summary>Current default-branch commit, or null when it could not be resolved.</summary>
        public string? CurrentCommitSha { get; set; } = null;

        /// <summary>True when the indexed commit differs from the current commit.</summary>
        public bool IsStale { get; set; } = false;

        /// <summary>Files changed between the indexed and current commit.</summary>
        public int ChangedFileCount { get; set; } = 0;

        /// <summary>Changed files that are indexable source (not docs, excluded paths, or non-source extensions).</summary>
        public int ChangedSourceFileCount { get; set; } = 0;

        /// <summary>
        /// True when the staleness matters: the index is stale AND at least one changed file is indexable
        /// source, OR the diff could not be computed (fail safe: treat unknown as relevant). A not-relevant
        /// stale index is one dispatch may ignore.
        /// </summary>
        public bool IsRelevant { get; set; } = false;

        /// <summary>True when the git diff between the two commits could not be computed; then IsRelevant is true by fail-safe.</summary>
        public bool DiffUnavailable { get; set; } = false;

        #endregion
    }
}
