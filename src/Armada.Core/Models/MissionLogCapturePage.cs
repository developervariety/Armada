namespace Armada.Core.Models
{
    using System;

    /// <summary>One bounded byte page of a mission-bound diagnostic log capture.</summary>
    public sealed class MissionLogCapturePage
    {
        /// <summary>Mission that owns this capture.</summary>
        public string MissionId { get; set; } = String.Empty;

        /// <summary>Stable capture identifier.</summary>
        public string CaptureId { get; set; } = String.Empty;

        /// <summary>Redacted text in this page.</summary>
        public string Content { get; set; } = String.Empty;

        /// <summary>Zero-based UTF-8 byte offset of this page.</summary>
        public long OffsetBytes { get; set; }

        /// <summary>UTF-8 byte length of this page.</summary>
        public int LengthBytes { get; set; }

        /// <summary>Total UTF-8 byte count of the capture.</summary>
        public long TotalUtf8Bytes { get; set; }

        /// <summary>SHA-256 of the complete capture content.</summary>
        public string Sha256 { get; set; } = String.Empty;

        /// <summary>True when additional pages remain.</summary>
        public bool HasMore { get; set; }

        /// <summary>True when the runner did not report possible output loss.</summary>
        public bool Complete { get; set; }

        /// <summary>True when the runner reached a per-stream byte limit.</summary>
        public bool RunnerOutputTruncated { get; set; }

        /// <summary>Bytes omitted by runner stream limits.</summary>
        public long RunnerOutputOmittedBytes { get; set; }

        /// <summary>True when timeout, drain or containment state may have omitted output.</summary>
        public bool RunnerOutputIncomplete { get; set; }

        /// <summary>Named error when the capture is missing or fails integrity checks.</summary>
        public string? Error { get; set; }
    }
}
