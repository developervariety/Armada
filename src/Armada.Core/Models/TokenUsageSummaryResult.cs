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
        /// Bucket size in minutes.
        /// </summary>
        public int BucketMinutes { get; set; }

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
