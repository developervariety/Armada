namespace Armada.Core.Models
{
    using System.Collections.Generic;

    /// <summary>
    /// A ranked impacted symbol from bounded graph traversal.
    /// </summary>
    public class CodeGraphImpactResult
    {
        #region Public-Members

        /// <summary>
        /// Traversal score. Higher is better.
        /// </summary>
        public double Score { get; set; } = 0;

        /// <summary>
        /// Minimum traversal depth at which this symbol was reached.
        /// </summary>
        public int MinDepth { get; set; } = 0;

        /// <summary>
        /// Number of traversal hits that reached this symbol.
        /// </summary>
        public int HitCount { get; set; } = 0;

        /// <summary>
        /// Impacted symbol record.
        /// </summary>
        public CodeGraphSymbolRecord Symbol { get; set; } = new CodeGraphSymbolRecord();

        /// <summary>
        /// Short reasons contributing to score.
        /// </summary>
        public List<string> Reasons { get; set; } = new List<string>();

        #endregion
    }
}
