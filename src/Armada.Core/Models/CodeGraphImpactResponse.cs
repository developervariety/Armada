namespace Armada.Core.Models
{
    using System.Collections.Generic;

    /// <summary>
    /// Response from bounded graph impact traversal.
    /// </summary>
    public class CodeGraphImpactResponse
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
        /// Effective traversal direction.
        /// </summary>
        public CodeGraphTraversalDirectionEnum Direction { get; set; } = CodeGraphTraversalDirectionEnum.Both;

        /// <summary>
        /// Effective traversal depth bound.
        /// </summary>
        public int MaxDepth { get; set; } = 3;

        /// <summary>
        /// Resolved seed symbols used for traversal.
        /// </summary>
        public List<CodeGraphSymbolRecord> ResolvedSeedSymbols { get; set; } = new List<CodeGraphSymbolRecord>();

        /// <summary>
        /// Ranked impacted symbols.
        /// </summary>
        public List<CodeGraphImpactResult> Results { get; set; } = new List<CodeGraphImpactResult>();

        /// <summary>
        /// Non-fatal warnings (for example stale or missing graph sidecars).
        /// </summary>
        public List<string> Warnings { get; set; } = new List<string>();

        #endregion
    }
}
