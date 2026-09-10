namespace Armada.Core.Models
{
    using Armada.Core.Enums;

    /// <summary>
    /// One stable, actionable result from an objective dispatch preview.
    /// </summary>
    public class ObjectiveDispatchPreviewIssue
    {
        /// <summary>
        /// Stable machine-readable issue code.
        /// </summary>
        public string Code { get; set; } = String.Empty;

        /// <summary>
        /// Area of dispatch readiness that produced the issue.
        /// </summary>
        public string Area { get; set; } = String.Empty;

        /// <summary>
        /// Issue severity. An Error makes the preview not ready.
        /// </summary>
        public ReadinessSeverityEnum Severity { get; set; } = ReadinessSeverityEnum.Warning;

        /// <summary>
        /// Operator-facing explanation.
        /// </summary>
        public string Message { get; set; } = String.Empty;

        /// <summary>
        /// Optional related identifier, path, ref, command, or value.
        /// </summary>
        public string? RelatedValue { get; set; } = null;
    }
}
