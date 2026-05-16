namespace Armada.Core.Models
{
    using Armada.Core.Enums;

    /// <summary>
    /// One landing-preview issue or warning.
    /// </summary>
    public class LandingPreviewIssue
    {
        /// <summary>
        /// Stable issue code.
        /// </summary>
        public string Code { get; set; } = string.Empty;

        /// <summary>
        /// Severity of the issue.
        /// </summary>
        public ReadinessSeverityEnum Severity { get; set; } = ReadinessSeverityEnum.Info;

        /// <summary>
        /// Short issue title.
        /// </summary>
        public string Title { get; set; } = string.Empty;

        /// <summary>
        /// Detailed issue message.
        /// </summary>
        public string Message { get; set; } = string.Empty;
    }
}
