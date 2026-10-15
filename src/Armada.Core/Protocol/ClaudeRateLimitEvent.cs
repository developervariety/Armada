namespace Armada.Core.Protocol
{
    using System;
    using System.Text.Json.Serialization;

    /// <summary>
    /// A Claude Code stream-json <c>rate_limit_event</c> line: the CLI reports the account's rate limit state between
    /// turns. It is not one of <see cref="ClaudeStreamLine.KnownTypes"/>, so it is parsed on its own.
    /// </summary>
    public class ClaudeRateLimitEvent
    {
        #region Public-Members

        /// <summary>
        /// The event type, rate_limit_event.
        /// </summary>
        public const string TypeRateLimitEvent = "rate_limit_event";

        /// <summary>
        /// Event type.
        /// </summary>
        [JsonPropertyName("type")]
        public string? Type { get; set; } = null;

        /// <summary>
        /// The rate limit state, or null.
        /// </summary>
        [JsonPropertyName("rate_limit_info")]
        public ClaudeRateLimitInfo? Info { get; set; } = null;

        #endregion

        #region Public-Methods

        /// <summary>
        /// Parse one output line. Returns false for anything that is not a rate_limit_event.
        /// </summary>
        /// <param name="line">Raw line.</param>
        /// <param name="value">The event, or null.</param>
        /// <returns>True when the line is a rate_limit_event.</returns>
        public static bool TryParse(string? line, out ClaudeRateLimitEvent? value)
        {
            if (RuntimeStreamJson.TryDeserializeLine<ClaudeRateLimitEvent>(line, out value)
                && value != null && String.Equals(value.Type, TypeRateLimitEvent, StringComparison.Ordinal))
                return true;

            value = null;
            return false;
        }

        #endregion
    }
}
