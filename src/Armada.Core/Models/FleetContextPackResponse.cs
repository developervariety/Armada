namespace Armada.Core.Models
{
    using System.Collections.Generic;

    /// <summary>
    /// Dispatch-ready fleet code context pack.
    /// </summary>
    public class FleetContextPackResponse
    {
        #region Public-Members

        /// <summary>
        /// Fleet identifier.
        /// </summary>
        public string FleetId { get; set; } = "";

        /// <summary>
        /// Mission goal.
        /// </summary>
        public string Goal { get; set; } = "";

        /// <summary>
        /// Markdown context pack.
        /// </summary>
        public string Markdown { get; set; } = "";

        /// <summary>
        /// Summarized version of the markdown context pack.
        /// </summary>
        public string? SummarizedMarkdown { get; set; } = null;

        /// <summary>
        /// True if the markdown was successfully summarized.
        /// </summary>
        public bool IsSummarized { get; set; } = false;

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
        /// Non-blocking warnings emitted during pack assembly.
        /// </summary>
        public List<string> Warnings { get; set; } = new List<string>();

        #endregion
    }
}
