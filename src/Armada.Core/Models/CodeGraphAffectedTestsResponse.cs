namespace Armada.Core.Models
{
    using System.Collections.Generic;

    /// <summary>
    /// Response for graph-based affected test recommendations.
    /// </summary>
    public class CodeGraphAffectedTestsResponse
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
        /// Effective traversal depth bound used for evidence gathering.
        /// </summary>
        public int MaxDepth { get; set; } = 3;

        /// <summary>
        /// Resolved seed symbols used for traversal.
        /// </summary>
        public List<CodeGraphSymbolRecord> ResolvedSeedSymbols { get; set; } = new List<CodeGraphSymbolRecord>();

        /// <summary>
        /// Ranked test candidates.
        /// </summary>
        public List<CodeGraphAffectedTestCandidate> Candidates { get; set; } = new List<CodeGraphAffectedTestCandidate>();

        /// <summary>
        /// Non-fatal warnings (for example stale or missing graph sidecars).
        /// </summary>
        public List<string> Warnings { get; set; } = new List<string>();

        #endregion
    }
}
