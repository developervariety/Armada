namespace Armada.Core.Models
{
    /// <summary>
    /// A scored code search result.
    /// </summary>
    public class CodeSearchResult
    {
        #region Public-Members

        /// <summary>
        /// Search score. Higher is better.
        /// </summary>
        public double Score { get; set; } = 0;

        /// <summary>
        /// Indexed record metadata and optional content.
        /// </summary>
        public CodeIndexRecord Record { get; set; } = new CodeIndexRecord();

        /// <summary>
        /// Short matching excerpt.
        /// </summary>
        public string Excerpt { get; set; } = "";

        #endregion
    }
}
