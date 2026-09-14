namespace Armada.Core.Models
{
    using System;

    /// <summary>
    /// Result of reading the durable restart record.
    /// </summary>
    public sealed class SelfDeployRestartRecordReadResult
    {
        /// <summary>
        /// Whether a record file exists.
        /// </summary>
        public bool Exists { get; set; }

        /// <summary>
        /// Parsed record when readable.
        /// </summary>
        public SelfDeployRestartRecord? Record { get; set; }

        /// <summary>
        /// Stable reason when a present record cannot be used.
        /// </summary>
        public string FailureReason { get; set; } = String.Empty;

        /// <summary>
        /// Whether a present record parsed and validated.
        /// </summary>
        public bool IsReadable => Exists && Record != null && String.IsNullOrWhiteSpace(FailureReason);
    }
}
