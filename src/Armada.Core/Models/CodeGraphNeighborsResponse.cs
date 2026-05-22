namespace Armada.Core.Models
{
    using System.Collections.Generic;

    /// <summary>
    /// Response for direct callers/callees graph queries.
    /// </summary>
    public class CodeGraphNeighborsResponse
    {
        #region Public-Members

        /// <summary>
        /// Index status used for this query.
        /// </summary>
        public CodeIndexStatus Status { get; set; } = new CodeIndexStatus();

        /// <summary>
        /// Original requested symbol text.
        /// </summary>
        public string RequestedSymbol { get; set; } = "";

        /// <summary>
        /// Resolved seed symbols used for edge matching.
        /// </summary>
        public List<CodeGraphSymbolRecord> ResolvedSeedSymbols { get; set; } = new List<CodeGraphSymbolRecord>();

        /// <summary>
        /// Ranked direct neighbors.
        /// </summary>
        public List<CodeGraphNeighborResult> Results { get; set; } = new List<CodeGraphNeighborResult>();

        /// <summary>
        /// Non-fatal warnings (for example stale or missing graph sidecars).
        /// </summary>
        public List<string> Warnings { get; set; } = new List<string>();

        #endregion
    }
}
