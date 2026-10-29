namespace Armada.Core.Protocol
{
    using System.Text.Json.Serialization;

    /// <summary>
    /// The arguments of pi's built-in tools that Armada shows in activity summaries: <c>bash</c> (<c>command</c>),
    /// <c>read</c> / <c>write</c> / <c>edit</c> / <c>ls</c> (<c>path</c>), <c>grep</c> / <c>find</c> (<c>pattern</c>).
    /// Other fields are ignored.
    /// </summary>
    public class PiToolArguments
    {
        #region Public-Members

        /// <summary>
        /// Shell command (bash), or null.
        /// </summary>
        [JsonPropertyName("command")]
        public string? Command { get; set; } = null;

        /// <summary>
        /// File or directory path, or null.
        /// </summary>
        [JsonPropertyName("path")]
        public string? Path { get; set; } = null;

        /// <summary>
        /// Search pattern (grep, find), or null.
        /// </summary>
        [JsonPropertyName("pattern")]
        public string? Pattern { get; set; } = null;

        #endregion
    }
}
