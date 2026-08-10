namespace Armada.Core.Models
{
    /// <summary>
    /// Result of a captain chat turn: the assistant reply plus the model that produced it and the
    /// per-turn timing/token metrics.
    /// </summary>
    public class CaptainChatResponse
    {
        #region Public-Members

        /// <summary>
        /// Whether the turn completed successfully.
        /// </summary>
        public bool Success { get; set; } = false;

        /// <summary>
        /// The assistant reply text (markdown). Empty on failure.
        /// </summary>
        public string Reply { get; set; } = string.Empty;

        /// <summary>
        /// The model that produced the reply (for example <c>gpt-oss:20b</c>).
        /// </summary>
        public string? Model { get; set; } = null;

        /// <summary>
        /// Per-turn timing and token statistics.
        /// </summary>
        public CaptainChatMetrics Metrics { get; set; } = new CaptainChatMetrics();

        /// <summary>
        /// Error message when <see cref="Success"/> is false.
        /// </summary>
        public string? Error { get; set; } = null;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        public CaptainChatResponse()
        {
        }

        #endregion
    }
}
