namespace Armada.Core.Models
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// Aggregate token-usage summary that feeds both dashboard charts: <see cref="Buckets"/> drives the
    /// time-series chart (usage over time, per model) and <see cref="ByModel"/> drives the horizontal
    /// aggregate chart (usage per model over the whole window). Grand totals and the estimated-record count
    /// support the summary tiles and the "includes estimates" note.
    /// </summary>
    public class TokenUsageSummaryResult
    {
        #region Public-Members

        /// <summary>
        /// Requested summary range start.
        /// </summary>
        public DateTime? FromUtc { get; set; } = null;

        /// <summary>
        /// Requested summary range end.
        /// </summary>
        public DateTime? ToUtc { get; set; } = null;

        /// <summary>
        /// Bucket size in minutes (may be fractional, for example 0.5 for 30-second buckets).
        /// </summary>
        public double BucketMinutes { get; set; }

        /// <summary>
        /// Number of usage records aggregated.
        /// </summary>
        public int RecordCount { get; set; }

        /// <summary>
        /// Number of aggregated records whose counts were estimated rather than measured.
        /// </summary>
        public int EstimatedCount { get; set; }

        /// <summary>
        /// Total input (prompt) tokens across the whole window.
        /// Sums every record's stored input, whatever its rule; see <see cref="LegacyRecordCount"/>.
        /// </summary>
        public long InputTokens { get; set; } = 0;

        /// <summary>
        /// Total output (completion) tokens across the whole window.
        /// </summary>
        public long OutputTokens { get; set; } = 0;

        /// <summary>
        /// Total cache-read tokens across the whole window.
        /// </summary>
        public long CachedTokens { get; set; } = 0;

        /// <summary>
        /// Total tokens across the whole window.
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
        /// Time buckets in chronological order (gap-filled across the requested window).
        /// </summary>
        public List<TokenUsageBucket> Buckets { get; set; } = new List<TokenUsageBucket>();

        /// <summary>
        /// Per-model aggregate over the whole window, ordered by total tokens descending (most-used first).
        /// </summary>
        public List<TokenUsageModelBreakdown> ByModel { get; set; } = new List<TokenUsageModelBreakdown>();

        #endregion
    }
}
