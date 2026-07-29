namespace Armada.Core.Models
{
    /// <summary>
    /// Dashboard summary of authoritative token telemetry.
    /// </summary>
    public class TokenUsageSummary
    {
        /// <summary>
        /// Beginning of the requested reporting window.
        /// </summary>
        public DateTime FromUtc { get; set; }

        /// <summary>
        /// End of the requested reporting window.
        /// </summary>
        public DateTime ToUtc { get; set; }

        /// <summary>
        /// Input tokens across all reported models.
        /// </summary>
        public long InputTokens { get; set; }

        /// <summary>
        /// Output tokens across all reported models.
        /// </summary>
        public long OutputTokens { get; set; }

        /// <summary>
        /// Reasoning tokens across all reported models.
        /// </summary>
        public long ReasoningTokens { get; set; }

        /// <summary>
        /// Cache-read tokens, shown separately from totals.
        /// </summary>
        public long CacheReadTokens { get; set; }

        /// <summary>
        /// Cache-write tokens, shown separately from totals.
        /// </summary>
        public long CacheWriteTokens { get; set; }

        /// <summary>
        /// Provider totals when supplied, otherwise input plus output. Reasoning and cache
        /// counters are reported separately because providers may include them in those categories.
        /// </summary>
        public long TotalTokens { get; set; }

        /// <summary>
        /// Number of authoritative usage samples.
        /// </summary>
        public long SampleCount { get; set; }

        /// <summary>
        /// Number of distinct missions with reported usage.
        /// </summary>
        public long ReportedMissionCount { get; set; }

        /// <summary>
        /// Human-readable coverage statement.
        /// </summary>
        public string CoverageNote { get; set; } = "";

        /// <summary>
        /// Runtime/model breakdown sorted by total token count.
        /// </summary>
        public List<TokenUsageModelBreakdown> Models { get; set; } = new List<TokenUsageModelBreakdown>();
    }
}
