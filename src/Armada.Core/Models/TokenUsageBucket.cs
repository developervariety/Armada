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
        /// Sums every record's stored input, whatever its rule; see <see cref="LegacyRecordCount"/>.
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
        /// Uncached input tokens, from records under the separate-input-buckets rule only.
        /// </summary>
        public long UncachedInputTokens { get; set; }

        /// <summary>
        /// Cache-read input tokens, from records under the separate-input-buckets rule only.
        /// </summary>
        public long CacheReadInputTokens { get; set; }

        /// <summary>
        /// Cache-write input tokens, from records under the separate-input-buckets rule only.
        /// </summary>
        public long CacheWriteInputTokens { get; set; }

        /// <summary>
        /// Input tokens of records under the legacy rule, whose input count means what each runtime's provider called
        /// input. Never added to the three input buckets.
        /// </summary>
        public long LegacyInputTokens { get; set; }

        /// <summary>
        /// Number of records under the legacy rule. When it is above zero, <c>InputTokens</c> and <c>TotalTokens</c>
        /// include input counted by that rule; the three input buckets never do.
        /// </summary>
        public long LegacyRecordCount { get; set; }

        /// <summary>
        /// Per-model token breakdown within this bucket, ordered by total tokens descending.
        /// </summary>
        public List<TokenUsageModelBreakdown> Models { get; set; } = new List<TokenUsageModelBreakdown>();

        #endregion
    }
}
