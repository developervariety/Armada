namespace Armada.Core.Models
{
    /// <summary>
    /// Aggregated authoritative token usage for one runtime and model.
    /// </summary>
    public class TokenUsageModelBreakdown
    {
        /// <summary>
        /// Runtime display name.
        /// </summary>
        public string Runtime { get; set; } = "";

        /// <summary>
        /// Model identifier.
        /// </summary>
        public string Model { get; set; } = "";

        /// <summary>
        /// Number of provider usage samples.
        /// </summary>
        public long SampleCount { get; set; }

        /// <summary>
        /// Number of distinct missions represented.
        /// </summary>
        public long MissionCount { get; set; }

        /// <summary>
        /// Input tokens.
        /// </summary>
        public long InputTokens { get; set; }

        /// <summary>
        /// Output tokens.
        /// </summary>
        public long OutputTokens { get; set; }

        /// <summary>
        /// Reasoning tokens.
        /// </summary>
        public long ReasoningTokens { get; set; }

        /// <summary>
        /// Cache-read tokens from every sample, whatever its rule. Under the separate-input-buckets rule they are part of
        /// input and total; under the legacy rule some runtimes counted them inside input and some did not.
        /// </summary>
        public long CacheReadTokens { get; set; }

        /// <summary>
        /// Cache-write tokens from every sample, whatever its rule. Under the separate-input-buckets rule they are part of
        /// input and total.
        /// </summary>
        public long CacheWriteTokens { get; set; }

        /// <summary>
        /// Provider total when supplied, otherwise input plus output. Reasoning is reported separately because
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
    }
}
