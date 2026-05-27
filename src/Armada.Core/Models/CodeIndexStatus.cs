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
        /// Fresh, Stale, Missing, Error, or Updating.
        /// </summary>
        public string Freshness { get; set; } = "Missing";

        /// <summary>
        /// True when this Admiral process is currently refreshing the vessel index.
        /// </summary>
        public bool UpdateInProgress { get; set; } = false;

        /// <summary>
        /// Timestamp when the current in-process refresh started.
        /// </summary>
        public DateTime? UpdateStartedUtc { get; set; } = null;

        /// <summary>
        /// Last heartbeat emitted by the current in-process refresh.
        /// </summary>
        public DateTime? UpdateHeartbeatUtc { get; set; } = null;

        /// <summary>
        /// Timestamp of the most recent completed embedding progress checkpoint.
        /// </summary>
        public DateTime? LastEmbeddingBatchUtc { get; set; } = null;

        /// <summary>
        /// Human-readable stage for the current in-process refresh.
        /// </summary>
        public string? UpdateStage { get; set; } = null;

        /// <summary>
        /// Completed work units in the current stage, when known.
        /// </summary>
        public int? UpdateProgressDone { get; set; } = null;

        /// <summary>
        /// Number of chunks embedded since this update started, when currently embedding.
        /// </summary>
        public int? ChunksEmbeddedSinceStart { get; set; } = null;

        /// <summary>
        /// Total work units in the current stage, when known.
        /// </summary>
        public int? UpdateProgressTotal { get; set; } = null;

        /// <summary>
        /// Percent complete for the current stage, when known.
        /// </summary>
        public double? UpdateProgressPercent { get; set; } = null;

        /// <summary>
        /// Number of files indexed.
        /// </summary>
        public int DocumentCount { get; set; } = 0;

        /// <summary>
        /// Number of indexed chunks.
        /// </summary>
        public int ChunkCount { get; set; } = 0;

        /// <summary>
        /// Fingerprint of settings that affect file inclusion and chunk boundaries.
        /// </summary>
        public string? IndexSettingsFingerprint { get; set; } = null;

        /// <summary>
        /// Fingerprint of settings that affect semantic embedding compatibility.
        /// </summary>
        public string? EmbeddingSettingsFingerprint { get; set; } = null;

        /// <summary>
        /// Whether semantic vectors were requested for this index run.
        /// </summary>
        public bool UseSemanticSearch { get; set; } = false;

        /// <summary>
        /// Embedding model used for vectors, when semantic search is enabled.
        /// </summary>
        public string? EmbeddingModel { get; set; } = null;

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
