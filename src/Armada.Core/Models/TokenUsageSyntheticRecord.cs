namespace Armada.Core.Models
{
    using System;

    /// <summary>Explicitly simulated token counts for an admin-authorized screenshot import.</summary>
    public class TokenUsageSyntheticRecord
    {
        /// <summary>Illustrative model identifier, not a provider billing claim.</summary>
        public string Model { get; set; } = "";
        /// <summary>Illustrative runtime identifier.</summary>
        public string? Runtime { get; set; }
        /// <summary>Historical UTC timestamp, no older than 366 days and not in the future.</summary>
        public DateTime CreatedUtc { get; set; }
        /// <summary>Non-negative simulated input tokens.</summary>
        public long InputTokens { get; set; }
        /// <summary>Non-negative simulated output tokens.</summary>
        public long OutputTokens { get; set; }
        /// <summary>Non-negative simulated cache-read subset of input tokens (informational).</summary>
        public long CachedTokens { get; set; }
    }
}
