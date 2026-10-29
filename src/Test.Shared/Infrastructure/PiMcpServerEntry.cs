namespace Test.Shared.Infrastructure
{
    using System.Collections.Generic;
    using System.Text.Json.Serialization;

    /// <summary>
    /// One server of a pi <c>mcp.json</c>.
    /// </summary>
    public class PiMcpServerEntry
    {
        #region Public-Members

        /// <summary>
        /// Transport type (<c>http</c>, <c>streamable-http</c>, <c>stdio</c>), or null.
        /// </summary>
        [JsonPropertyName("type")]
        public string? Type { get; set; } = null;

        /// <summary>
        /// Server URL, or null.
        /// </summary>
        [JsonPropertyName("url")]
        public string? Url { get; set; } = null;

        /// <summary>
        /// Stdio command, or null.
        /// </summary>
        [JsonPropertyName("command")]
        public string? Command { get; set; } = null;

        /// <summary>
        /// HTTP headers, or null.
        /// </summary>
        [JsonPropertyName("headers")]
        public Dictionary<string, string>? Headers { get; set; } = null;

        /// <summary>
        /// Tool exposure, or null.
        /// </summary>
        [JsonPropertyName("exposure")]
        public string? Exposure { get; set; } = null;

        /// <summary>
        /// Server description, or null.
        /// </summary>
        [JsonPropertyName("description")]
        public string? Description { get; set; } = null;

        /// <summary>
        /// Whether the server is enabled, or null.
        /// </summary>
        [JsonPropertyName("enabled")]
        public bool? Enabled { get; set; } = null;

        #endregion
    }
}
