namespace Armada.Core.Models
{
    using System;

    /// <summary>Identity and integrity metadata for one persisted mission log capture.</summary>
    public sealed class MissionLogCaptureMetadata
    {
        /// <summary>Unique mission-bound capture identifier.</summary>
        public string CaptureId { get; set; } = String.Empty;

        /// <summary>UTF-8 byte count of the complete redacted output available from the runner.</summary>
        public long TotalUtf8Bytes { get; set; }

        /// <summary>SHA-256 digest of the complete redacted output available from the runner.</summary>
        public string Sha256 { get; set; } = String.Empty;

        /// <summary>True when the process runner omitted output at its per-stream cap.</summary>
        public bool RunnerOutputTruncated { get; set; }

        /// <summary>UTF-8 bytes omitted by the process runner.</summary>
        public long RunnerOutputOmittedBytes { get; set; }

        /// <summary>True when the runner did not observe a clean, complete process output stream.</summary>
        public bool RunnerOutputIncomplete { get; set; }
    }
}
