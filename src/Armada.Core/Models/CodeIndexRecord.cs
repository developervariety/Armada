namespace Armada.Core.Models
{
    /// <summary>
    /// A single indexed code or documentation chunk.
    /// </summary>
    public class CodeIndexRecord
    {
        #region Public-Members

        /// <summary>
        /// Vessel identifier.
        /// </summary>
        public string VesselId { get; set; } = "";

        /// <summary>
        /// Repository-relative path.
        /// </summary>
        public string Path { get; set; } = "";

        /// <summary>
        /// Default-branch commit SHA represented by this record.
        /// </summary>
        public string CommitSha { get; set; } = "";

        /// <summary>
        /// SHA-256 hash of the full file content.
        /// </summary>
        public string ContentHash { get; set; } = "";

        /// <summary>
        /// Detected language or file kind.
        /// </summary>
        public string Language { get; set; } = "";

        /// <summary>
        /// Start line in the source file, 1-based.
        /// </summary>
        public int StartLine { get; set; } = 1;

        /// <summary>
        /// End line in the source file, 1-based.
        /// </summary>
        public int EndLine { get; set; } = 1;

        /// <summary>
        /// Freshness at the time this record is returned.
        /// </summary>
        public string Freshness { get; set; } = "Fresh";

        /// <summary>
        /// Timestamp when this record was indexed.
        /// </summary>
        public DateTime IndexedAtUtc { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// True when this record came from a path marked reference-only.
        /// </summary>
        public bool IsReferenceOnly { get; set; } = false;

        /// <summary>
        /// Chunk text.
        /// </summary>
        public string Content { get; set; } = "";

        #endregion
    }
}
