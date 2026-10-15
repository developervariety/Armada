namespace Armada.Core.Protocol
{
    using System.Collections.Generic;
    using System.Text.Json.Serialization;

    /// <summary>
    /// The arguments of a Mux built-in tool call (the <c>arguments</c> object of a <c>tool_call_proposed</c> event's
    /// <see cref="MuxToolCall"/>), as far as a one-line activity summary needs them: the file of read_file, write_file,
    /// edit_file and multi_edit, the command of run_process, the pattern and path of grep and glob, the path of
    /// list_directory, and the URL or query of web_retrieve and web_search. Every field is null when absent.
    /// </summary>
    public class MuxToolInput
    {
        #region Public-Members

        /// <summary>
        /// File path (read_file, write_file, edit_file, multi_edit, delete_file, file_metadata), or null.
        /// </summary>
        [JsonPropertyName("file_path")]
        public string? FilePath { get; set; } = null;

        /// <summary>
        /// Command (run_process), or null.
        /// </summary>
        [JsonPropertyName("command")]
        public string? Command { get; set; } = null;

        /// <summary>
        /// Command arguments (run_process), or null.
        /// </summary>
        [JsonPropertyName("args")]
        public List<string>? Args { get; set; } = null;

        /// <summary>
        /// Search pattern (grep, glob), or null.
        /// </summary>
        [JsonPropertyName("pattern")]
        public string? Pattern { get; set; } = null;

        /// <summary>
        /// Path (grep, glob, list_directory, manage_directory), or null.
        /// </summary>
        [JsonPropertyName("path")]
        public string? Path { get; set; } = null;

        /// <summary>
        /// URL (web_retrieve), or null.
        /// </summary>
        [JsonPropertyName("url")]
        public string? Url { get; set; } = null;

        /// <summary>
        /// Query (web_search), or null.
        /// </summary>
        [JsonPropertyName("query")]
        public string? Query { get; set; } = null;

        #endregion
    }
}
