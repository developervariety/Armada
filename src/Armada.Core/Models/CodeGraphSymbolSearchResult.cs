namespace Armada.Core.Models
{
    /// <summary>
    /// A scored symbol search result.
    /// </summary>
    public class CodeGraphSymbolSearchResult
    {
        #region Public-Members

        /// <summary>
        /// Search score. Higher is better.
        /// </summary>
        public double Score { get; set; } = 0;

        /// <summary>
        /// Short reason for the match.
        /// </summary>
        public string MatchReason { get; set; } = "";

        /// <summary>
        /// Matched symbol record.
        /// </summary>
        public CodeGraphSymbolRecord Symbol { get; set; } = new CodeGraphSymbolRecord();

        #endregion
    }
}
