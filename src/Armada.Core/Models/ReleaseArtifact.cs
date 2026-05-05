namespace Armada.Core.Models
{
    using System;

    /// <summary>
    /// Artifact associated with a release, typically derived from a linked check run.
    /// </summary>
    public class ReleaseArtifact
    {
        /// <summary>
        /// Source record type, such as CheckRun.
        /// </summary>
        public string SourceType { get; set; } = "CheckRun";

        /// <summary>
        /// Source record identifier.
        /// </summary>
        public string? SourceId { get; set; } = null;

        /// <summary>
        /// Artifact path or label.
        /// </summary>
        public string Path { get; set; } = String.Empty;

        /// <summary>
        /// Artifact size in bytes.
        /// </summary>
        public long SizeBytes { get; set; } = 0;

        /// <summary>
        /// Artifact last-write timestamp in UTC when known.
        /// </summary>
        public DateTime? LastWriteUtc { get; set; } = null;
    }
}
