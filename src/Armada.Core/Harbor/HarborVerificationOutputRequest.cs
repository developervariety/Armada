namespace Armada.Core.Harbor
{
    using System;

    /// <summary>Reads one bounded chunk from a completed verification's runner-owned output spool.</summary>
    public sealed class HarborVerificationOutputRequest
    {
        /// <summary>Opaque output spool identity returned by the verification operation.</summary>
        public string OutputId { get; set; } = String.Empty;

        /// <summary>Verification identity bound to the output spool.</summary>
        public string CheckId { get; set; } = String.Empty;

        /// <summary>True to read standard error; false to read standard output.</summary>
        public bool StandardError { get; set; }

        /// <summary>Zero-based byte offset in the selected stream.</summary>
        public long Offset { get; set; }

        /// <summary>Maximum bytes returned in this chunk.</summary>
        public int MaxBytes { get; set; } = 1024 * 1024;
    }
}
