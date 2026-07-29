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
        /// Cache-read tokens. These are shown separately and are not added to total tokens.
        /// </summary>
        public long CacheReadTokens { get; set; }

        /// <summary>
        /// Cache-write tokens. These are shown separately and are not added to total tokens.
        /// </summary>
        public long CacheWriteTokens { get; set; }

        /// <summary>
        /// Provider total when supplied, otherwise input plus output. Reasoning and cache
        /// counters are reported separately because providers may include them in those categories.
        /// </summary>
        public long TotalTokens { get; set; }
    }
}
