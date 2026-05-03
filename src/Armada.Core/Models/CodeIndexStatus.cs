namespace Armada.Core.Models
{
    /// <summary>
    /// Status metadata for a vessel code index.
    /// </summary>
    public class CodeIndexStatus
    {
        #region Public-Members

        /// <summary>
        /// Vessel identifier.
        /// </summary>
        public string VesselId { get; set; } = "";

        /// <summary>
        /// Vessel name.
        /// </summary>
        public string VesselName { get; set; } = "";

        /// <summary>
        /// Default branch indexed for the vessel.
        /// </summary>
        public string DefaultBranch { get; set; } = "";

        /// <summary>
        /// Commit SHA represented by the index.
        /// </summary>
        public string? IndexedCommitSha { get; set; } = null;

        /// <summary>
        /// Current default-branch commit SHA, when it could be resolved.
        /// </summary>
        public string? CurrentCommitSha { get; set; } = null;

        /// <summary>
        /// Timestamp when the index was last updated.
        /// </summary>
        public DateTime? IndexedAtUtc { get; set; } = null;

        /// <summary>
        /// Fresh, Stale, Missing, or Error.
        /// </summary>
        public string Freshness { get; set; } = "Missing";

        /// <summary>
        /// Number of files indexed.
        /// </summary>
        public int DocumentCount { get; set; } = 0;

        /// <summary>
        /// Number of indexed chunks.
        /// </summary>
        public int ChunkCount { get; set; } = 0;

        /// <summary>
        /// Absolute path to the Admiral-owned index directory for this vessel.
        /// </summary>
        public string IndexDirectory { get; set; } = "";

        /// <summary>
        /// Last indexing error, if any.
        /// </summary>
        public string? LastError { get; set; } = null;

        #endregion
    }
}
