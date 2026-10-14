namespace Armada.Core.Models
{
    using System;

    /// <summary>Receipt for a synthetic import; the operation never invokes an inference provider or dispatches work.</summary>
    public class TokenUsageSyntheticResult
    {
        /// <summary>Batch identifier.</summary>
        public string BatchId { get; set; } = "";
        /// <summary>Persisted source id for inspecting or explicitly removing this batch.</summary>
        public string SourceId { get; set; } = "";
        /// <summary>New rows created.</summary>
        public int Created { get; set; }
        /// <summary>Matching rows already present.</summary>
        public int Existing { get; set; }
        /// <summary>First simulated timestamp.</summary>
        public DateTime FromUtc { get; set; }
        /// <summary>Last simulated timestamp.</summary>
        public DateTime ToUtc { get; set; }
        /// <summary>Token sum in this batch, including matching existing rows.</summary>
        public long TotalTokens { get; set; }
    }
}
