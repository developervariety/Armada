namespace Armada.Core.Models
{
    /// <summary>
    /// One page of the authoritative safe output artifact captured for a mission.
    /// </summary>
    public sealed class MissionOutputArtifactPage
    {
        /// <summary>Historical attempt metadata, present only on an explicitly addressed Research archive page.</summary>
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public ResearchReplayStage? PriorResearchAttempt { get; set; }
        /// <summary>Mission that owns the persisted output.</summary>
        public string MissionId { get; set; } = String.Empty;
        /// <summary>Stable reference used in pipeline handoffs.</summary>
        public string ArtifactRef { get; set; } = String.Empty;
        /// <summary>Actual zero-based UTF-16 character offset for this page.</summary>
        public int Offset { get; set; }
        /// <summary>Number of UTF-16 characters in this page.</summary>
        public int Length { get; set; }
        /// <summary>UTF-16 character count of the joined content of every page; the same on every page.</summary>
        public int TotalLength { get; set; }
        /// <summary>UTF-8 byte count of the joined content of every page; the same on every page.</summary>
        public int TotalUtf8Bytes { get; set; }
        /// <summary>Page content.</summary>
        public string Content { get; set; } = String.Empty;
        /// <summary>True when more output follows this page.</summary>
        public bool HasMore { get; set; }
        /// <summary>Offset for the next page, or null at the end.</summary>
        public int? NextOffset { get; set; }
        /// <summary>
        /// SHA-256 digest of the joined content of every page encoded as UTF-8; the same on every page. Join the
        /// pages' <see cref="Content"/> in offset order and hash the result to verify it.
        /// </summary>
        public string Sha256 { get; set; } = String.Empty;
        /// <summary>SHA-256 digest of this page's <see cref="Content"/> alone, encoded as UTF-8.</summary>
        public string PageSha256 { get; set; } = String.Empty;
        /// <summary>UTF-8 byte count of this page's <see cref="Content"/> alone.</summary>
        public int PageUtf8Bytes { get; set; }
        /// <summary>True when the mission run has stopped producing output.</summary>
        public bool Finalized { get; set; }
        /// <summary>True when the complete output is available and was not clipped during capture.</summary>
        public bool Complete { get; set; }
        /// <summary>Machine-readable reason why the artifact is not complete.</summary>
        public string? TruncationReason { get; set; }
        /// <summary>
        /// Report sections the stage's persona must include that a finished successful report lacks. Empty when
        /// the persona has no fixed report contract or the report holds every section.
        /// </summary>
        public List<string> MissingReportSections { get; set; } = new List<string>();
    }
}
