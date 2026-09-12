namespace Armada.Core.Models
{
    /// <summary>
    /// One page of the authoritative safe output artifact captured for a mission.
    /// </summary>
    public sealed class MissionOutputArtifactPage
    {
        /// <summary>Mission that owns the persisted output.</summary>
        public string MissionId { get; set; } = String.Empty;
        /// <summary>Stable reference used in pipeline handoffs.</summary>
        public string ArtifactRef { get; set; } = String.Empty;
        /// <summary>Actual zero-based UTF-16 character offset for this page.</summary>
        public int Offset { get; set; }
        /// <summary>Number of UTF-16 characters in this page.</summary>
        public int Length { get; set; }
        /// <summary>Total UTF-16 character count.</summary>
        public int TotalLength { get; set; }
        /// <summary>Total byte count after UTF-8 encoding.</summary>
        public int TotalUtf8Bytes { get; set; }
        /// <summary>Page content.</summary>
        public string Content { get; set; } = String.Empty;
        /// <summary>True when more output follows this page.</summary>
        public bool HasMore { get; set; }
        /// <summary>Offset for the next page, or null at the end.</summary>
        public int? NextOffset { get; set; }
        /// <summary>SHA-256 digest of the complete persisted output encoded as UTF-8.</summary>
        public string Sha256 { get; set; } = String.Empty;
        /// <summary>True when the mission run has stopped producing output.</summary>
        public bool Finalized { get; set; }
        /// <summary>True when the complete output is available and was not clipped during capture.</summary>
        public bool Complete { get; set; }
        /// <summary>Machine-readable reason why the artifact is not complete.</summary>
        public string? TruncationReason { get; set; }
    }
}
