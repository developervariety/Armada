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

        #endregion
    }
}
