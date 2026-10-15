namespace Armada.Core.Protocol
{
    using System.Text.Json.Serialization;

    /// <summary>
    /// The <c>rate_limit_info</c> object of a Claude Code stream-json <c>rate_limit_event</c>: the account's rate limit
    /// state, when it resets, and which window it is.
    /// </summary>
    public class ClaudeRateLimitInfo
    {
        #region Public-Members

        /// <summary>
        /// Status: allowed, allowed_warning, or rejected; or null.
        /// </summary>
        [JsonPropertyName("status")]
        public string? Status { get; set; } = null;

        /// <summary>
        /// When the limit resets, in Unix epoch seconds, or null.
        /// </summary>
        [JsonPropertyName("resetsAt")]
        public long? ResetsAt { get; set; } = null;

        /// <summary>
        /// Which limit window (for example five_hour or seven_day), or null.
        /// </summary>
        [JsonPropertyName("rateLimitType")]
        public string? RateLimitType { get; set; } = null;

        /// <summary>
        /// Fraction of the limit used (0 to 1), or null.
        /// </summary>
        [JsonPropertyName("utilization")]
        public double? Utilization { get; set; } = null;

        #endregion
    }
}
