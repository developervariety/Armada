namespace Armada.Core.Models
{
    /// <summary>
    /// Authoritative token usage reported by an agent runtime.
    /// </summary>
    public class RuntimeTokenUsage
    {
        /// <summary>
        /// Runtime that emitted the usage record.
        /// </summary>
        public string Runtime { get; set; } = "";

        /// <summary>
        /// Model identifier used for the request.
        /// </summary>
        public string Model { get; set; } = "";

        /// <summary>
        /// Authoritative telemetry source.
        /// </summary>
        public string Source { get; set; } = "";

        /// <summary>
        /// Input tokens reported by the provider.
        /// </summary>
        public long InputTokens { get; set; }

        /// <summary>
        /// Output tokens reported by the provider.
        /// </summary>
        public long OutputTokens { get; set; }

        /// <summary>
        /// Reasoning tokens reported by the provider.
        /// </summary>
        public long ReasoningTokens { get; set; }

        /// <summary>
        /// Cache-read tokens reported by the provider.
        /// </summary>
        public long CacheReadTokens { get; set; }

        /// <summary>
        /// Cache-write tokens reported by the provider.
        /// </summary>
        public long CacheWriteTokens { get; set; }

        /// <summary>
        /// Total tokens reported directly by the provider, when available.
        /// </summary>
        public long? ProviderTotalTokens { get; set; }
    }
}
