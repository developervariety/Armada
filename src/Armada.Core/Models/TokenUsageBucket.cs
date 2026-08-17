namespace Armada.Core.Models
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// One time bucket in a token-usage summary: bucket-level totals plus the per-model breakdown that lets
    /// the time-series chart stack bars or draw a line per model.
    /// </summary>
    public class TokenUsageBucket
    {
        #region Public-Members

        /// <summary>
        /// Inclusive bucket start (UTC).
        /// </summary>
        public DateTime BucketStartUtc { get; set; }

        /// <summary>
        /// Exclusive bucket end (UTC).
        /// </summary>
        public DateTime BucketEndUtc { get; set; }

        /// <summary>
        /// Input (prompt) tokens across all models in the bucket.
        /// </summary>
        public long InputTokens { get; set; } = 0;

        /// <summary>
        /// Output (completion) tokens across all models in the bucket.
        /// </summary>
        public long OutputTokens { get; set; } = 0;

        /// <summary>
        /// Cache-read tokens across all models in the bucket.
        /// </summary>
        public long CachedTokens { get; set; } = 0;

        /// <summary>
        /// Total tokens across all models in the bucket.
        /// </summary>
        public long TotalTokens { get; set; } = 0;

        /// <summary>
        /// Per-model token breakdown within this bucket, ordered by total tokens descending.
        /// </summary>
        public List<TokenUsageModelBreakdown> Models { get; set; } = new List<TokenUsageModelBreakdown>();

        #endregion
    }
}
