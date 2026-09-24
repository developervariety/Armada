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
        /// Cache-read tokens from every sample, whatever its rule. Under the separate-input-buckets rule they are part of
        /// input and total.
        /// </summary>
        public long CacheReadTokens { get; set; }

        /// <summary>
        /// Cache-write tokens from every sample, whatever its rule. Under the separate-input-buckets rule they are part of
        /// input and total.
        /// </summary>
        public long CacheWriteTokens { get; set; }

        /// <summary>
        /// Provider totals when supplied, otherwise input plus output. Reasoning is reported separately because
        /// providers may include it in output.
        /// </summary>
        public long TotalTokens { get; set; }

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
