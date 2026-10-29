namespace Armada.Core.Protocol
{
    using System.Text.Json.Serialization;

    /// <summary>
    /// Token usage of one pi model response. On an assistant <c>message_end</c> it is final for that response; on
    /// <c>message_update</c> it is cumulative for the response in progress and must not be summed. <see cref="Reasoning"/>
    /// is already included in <see cref="Output"/>.
    /// </summary>
    public class PiUsage
    {
        #region Public-Members

        /// <summary>
        /// Uncached input tokens, or null.
        /// </summary>
        [JsonPropertyName("input")]
        public long? Input { get; set; } = null;

        /// <summary>
        /// Output tokens (reasoning included), or null.
        /// </summary>
        [JsonPropertyName("output")]
        public long? Output { get; set; } = null;

        /// <summary>
        /// Cache-read input tokens, or null.
        /// </summary>
        [JsonPropertyName("cacheRead")]
        public long? CacheRead { get; set; } = null;

        /// <summary>
        /// Cache-write input tokens, or null.
        /// </summary>
        [JsonPropertyName("cacheWrite")]
        public long? CacheWrite { get; set; } = null;

        /// <summary>
        /// Reasoning tokens (a subset of <see cref="Output"/>), or null.
        /// </summary>
        [JsonPropertyName("reasoning")]
        public long? Reasoning { get; set; } = null;

        /// <summary>
        /// Total tokens, or null.
        /// </summary>
        [JsonPropertyName("totalTokens")]
        public long? TotalTokens { get; set; } = null;

        /// <summary>
        /// Cost breakdown, or null.
        /// </summary>
        [JsonPropertyName("cost")]
        public PiCost? Cost { get; set; } = null;

        #endregion
    }
}
