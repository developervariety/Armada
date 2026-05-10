namespace Armada.Core.Models
{
    using System.Collections.Generic;

    /// <summary>
    /// Dispatch-ready code context pack.
    /// </summary>
    public class ContextPackResponse
    {
        #region Public-Members

        /// <summary>
        /// Index status used to build the pack.
        /// </summary>
        public CodeIndexStatus Status { get; set; } = new CodeIndexStatus();

        /// <summary>
        /// Mission goal.
        /// </summary>
        public string Goal { get; set; } = "";

        /// <summary>
        /// Markdown context pack.
        /// </summary>
        public string Markdown { get; set; } = "";

        /// <summary>
        /// Rough token estimate based on character length.
        /// </summary>
        public int EstimatedTokens { get; set; } = 0;

        /// <summary>
        /// Absolute path to the materialized context pack on the Admiral host.
        /// </summary>
        public string MaterializedPath { get; set; } = "";

        /// <summary>
        /// Suggested prestagedFiles entry for armada_dispatch.
        /// </summary>
        public List<PrestagedFile> PrestagedFiles { get; set; } = new List<PrestagedFile>();

        /// <summary>
        /// Evidence results included in the markdown.
        /// </summary>
        public List<CodeSearchResult> Results { get; set; } = new List<CodeSearchResult>();

        /// <summary>
        /// Identifiers of <c>vessel_pack_hints</c> rows that fired during pre-selection (v2-F1).
        /// Empty when no pack hints matched the goal text. Returned as an empty array (not null)
        /// for consistent caller handling.
        /// </summary>
        public List<string> MatchedHintIds { get; set; } = new List<string>();

        /// <summary>
        /// Non-blocking warnings emitted during pack assembly (v2-F1). Examples:
        /// "hard_include_truncated: forced-include files exceeded token budget; truncated by priority order".
        /// </summary>
        public List<string> Warnings { get; set; } = new List<string>();

        #endregion
    }
}
