namespace Armada.Core.Protocol
{
    using System.Text.Json.Serialization;

    /// <summary>
    /// Token usage mux reports for a run (structured output contract version 2), on the <c>run_completed</c> event and
    /// the <c>json</c> run summary. Absent when mux runs with <c>--no-stats</c> or speaks contract version 1.
    /// </summary>
    public class MuxRunUsage
    {
        #region Public-Members

        /// <summary>
        /// Prompt tokens the provider reported, or null.
        /// </summary>
        [JsonPropertyName("inputTokens")]
        public long? InputTokens { get; set; } = null;

        /// <summary>
        /// Completion tokens the provider reported, or null.
        /// </summary>
        [JsonPropertyName("outputTokens")]
        public long? OutputTokens { get; set; } = null;

        /// <summary>
        /// Prompt tokens served from the provider's cache, or null.
        /// </summary>
        [JsonPropertyName("cachedTokens")]
        public long? CachedTokens { get; set; } = null;

        /// <summary>
        /// Reasoning tokens, or null.
        /// </summary>
        [JsonPropertyName("reasoningTokens")]
        public long? ReasoningTokens { get; set; } = null;

        /// <summary>
        /// Total tokens (input plus output), or null.
        /// </summary>
        [JsonPropertyName("totalTokens")]
        public long? TotalTokens { get; set; } = null;

        /// <summary>
        /// Tokens mux estimated where the provider reported none, or null.
        /// </summary>
        [JsonPropertyName("estimatedTokens")]
        public long? EstimatedTokens { get; set; } = null;

        /// <summary>
        /// Cost in US dollars from mux's pricing table, or null.
        /// </summary>
        [JsonPropertyName("costUsd")]
        public double? CostUsd { get; set; } = null;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        public MuxRunUsage()
        {
        }

        #endregion
    }
}
