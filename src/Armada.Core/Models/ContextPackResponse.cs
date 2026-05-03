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

        #endregion
    }
}
