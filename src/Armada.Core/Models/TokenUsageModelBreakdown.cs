namespace Armada.Core.Models
{
    /// <summary>
    /// Token counts for a single model, used both for the per-model slice inside a time bucket and for the
    /// whole-window per-model aggregate that drives the horizontal bar chart.
    /// </summary>
    public class TokenUsageModelBreakdown
    {
        #region Public-Members

        /// <summary>
        /// Model identifier (for example "claude-sonnet-4"). "unknown" when the runtime did not report one.
        /// </summary>
        public string Model { get; set; } = string.Empty;

        /// <summary>
        /// Input (prompt) tokens.
        /// </summary>
        public long InputTokens { get; set; } = 0;

        /// <summary>
        /// Output (completion) tokens.
        /// </summary>
        public long OutputTokens { get; set; } = 0;

        /// <summary>
        /// Cache-read tokens.
        /// </summary>
        public long CachedTokens { get; set; } = 0;

        /// <summary>
        /// Total tokens.
        /// </summary>
        public long TotalTokens { get; set; } = 0;

        #endregion
    }
}
