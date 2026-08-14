namespace Armada.Core.Models
{
    /// <summary>
    /// Timing and token statistics for a single captain chat turn, surfaced in the dashboard so an
    /// operator can see how the model performed. Values are populated from the provider's own usage
    /// report where available (Ollama reports nanosecond-precision durations and token counts), so
    /// tokens-per-second and durations reflect server-measured work rather than a client estimate.
    /// </summary>
    public class CaptainChatMetrics
    {
        #region Public-Members

        /// <summary>
        /// Approximate time to first token in milliseconds. Derived from the provider's prompt-eval
        /// duration when reported; otherwise the wall-clock time until the response began.
        /// </summary>
        public double? TimeToFirstTokenMs { get; set; } = null;

        /// <summary>
        /// Time spent generating (streaming) the completion, in milliseconds. From the provider's eval
        /// duration when reported.
        /// </summary>
        public double? StreamingMs { get; set; } = null;

        /// <summary>
        /// Total wall-clock time for the turn in milliseconds (request sent to response complete).
        /// </summary>
        public double? TotalMs { get; set; } = null;

        /// <summary>
        /// Number of prompt (input) tokens, when reported by the provider.
        /// </summary>
        public int? PromptTokens { get; set; } = null;

        /// <summary>
        /// Number of completion (output) tokens, when reported by the provider.
        /// </summary>
        public int? CompletionTokens { get; set; } = null;

        /// <summary>
        /// Total tokens (prompt + completion), when reported by the provider.
        /// </summary>
        public int? TotalTokens { get; set; } = null;

        /// <summary>
        /// Output tokens per second over the generation window, when both the completion token count and
        /// the generation duration are known.
        /// </summary>
        public double? TokensPerSecond { get; set; } = null;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        public CaptainChatMetrics()
        {
        }

        #endregion
    }
}
