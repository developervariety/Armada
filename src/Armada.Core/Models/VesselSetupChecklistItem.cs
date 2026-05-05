namespace Armada.Core.Models
{
    using Armada.Core.Enums;

    /// <summary>
    /// One actionable setup checklist item for a vessel.
    /// </summary>
    public class VesselSetupChecklistItem
    {
        /// <summary>
        /// Stable checklist code.
        /// </summary>
        public string Code { get; set; } = string.Empty;

        /// <summary>
        /// Suggested severity or urgency for the item.
        /// </summary>
        public ReadinessSeverityEnum Severity { get; set; } = ReadinessSeverityEnum.Info;

        /// <summary>
        /// Short checklist label.
        /// </summary>
        public string Title { get; set; } = string.Empty;

        /// <summary>
        /// Actionable checklist guidance.
        /// </summary>
        public string Message { get; set; } = string.Empty;

        /// <summary>
        /// Whether this checklist item is already satisfied.
        /// </summary>
        public bool IsSatisfied { get; set; } = false;

        /// <summary>
        /// Optional action button label.
        /// </summary>
        public string? ActionLabel { get; set; } = null;

        /// <summary>
        /// Optional dashboard route that helps resolve the item.
        /// </summary>
        public string? ActionRoute { get; set; } = null;
    }
}
