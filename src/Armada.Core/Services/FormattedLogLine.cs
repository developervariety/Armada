namespace Armada.Core.Services
{
    /// <summary>
    /// One formatted captain-log line: the display text plus flags describing what the formatter did with
    /// the raw line (resolved a tool name, redacted a secret, truncated an oversized payload, or dropped a
    /// noise line).
    /// </summary>
    public sealed class FormattedLogLine
    {
        #region Public-Members

        /// <summary>
        /// The formatted display text. Empty when <see cref="Dropped"/> is true.
        /// </summary>
        public string Text { get; set; } = "";

        /// <summary>
        /// True when the line represents a tool call resolved out of a runtime's structured event.
        /// </summary>
        public bool IsToolCall { get; set; } = false;

        /// <summary>
        /// The resolved tool name when <see cref="IsToolCall"/> is true; null otherwise.
        /// </summary>
        public string? ToolName { get; set; } = null;

        /// <summary>
        /// True when at least one secret-shaped value was redacted from the line.
        /// </summary>
        public bool Redacted { get; set; } = false;

        /// <summary>
        /// True when the line was truncated because it exceeded the length cap.
        /// </summary>
        public bool Truncated { get; set; } = false;

        /// <summary>
        /// True when the line was dropped as noise and should not be displayed.
        /// </summary>
        public bool Dropped { get; set; } = false;

        #endregion
    }
}
