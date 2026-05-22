namespace Armada.Core.Models
{
    using System.Collections.Generic;

    /// <summary>
    /// Response from symbol search against graph sidecars.
    /// </summary>
    public class CodeGraphSymbolSearchResponse
    {
        #region Public-Members

        /// <summary>
        /// Index status used for this query.
        /// </summary>
        public CodeIndexStatus Status { get; set; } = new CodeIndexStatus();

        /// <summary>
        /// Original query text.
        /// </summary>
        public string Query { get; set; } = "";

        /// <summary>
        /// Ranked matching symbols.
        /// </summary>
        public List<CodeGraphSymbolSearchResult> Results { get; set; } = new List<CodeGraphSymbolSearchResult>();

        /// <summary>
        /// Non-fatal warnings (for example stale or missing graph sidecars).
        /// </summary>
        public List<string> Warnings { get; set; } = new List<string>();

        #endregion
    }
}
