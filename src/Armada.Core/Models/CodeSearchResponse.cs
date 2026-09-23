namespace Armada.Core.Models
{
    using System.Collections.Generic;

    /// <summary>
    /// Response from a vessel code index search.
    /// </summary>
    public class CodeSearchResponse
    {
        #region Public-Members

        /// <summary>
        /// Unavailable reason for a vessel that has no code index. Only an explicit index update
        /// creates an index, so search and context-pack generation never index such a vessel.
        /// </summary>
        public const string NotIndexedReason = "not_indexed";

        /// <summary>
        /// Index status used for this search.
        /// </summary>
        public CodeIndexStatus Status { get; set; } = new CodeIndexStatus();

        /// <summary>
        /// Original query text.
        /// </summary>
        public string Query { get; set; } = "";

        /// <summary>
        /// Matching results.
        /// </summary>
        public List<CodeSearchResult> Results { get; set; } = new List<CodeSearchResult>();

        /// <summary>
        /// False when no search ran. An unavailable response is never an empty result set: the
        /// reason and message say why nothing was searched.
        /// </summary>
        public bool Available { get; set; } = true;

        /// <summary>
        /// Machine-readable reason no search ran, for example <c>not_indexed</c> when the vessel has
        /// no code index. Null when the search ran.
        /// </summary>
        public string? UnavailableReason { get; set; } = null;

        /// <summary>
        /// Human-readable explanation when no search ran. Null when the search ran.
        /// </summary>
        public string? Message { get; set; } = null;

        #endregion
    }
}
