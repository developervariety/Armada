namespace Armada.Core.Models
{
    using Armada.Core.Enums;

    /// <summary>
    /// One resolved command preview entry within a workflow profile.
    /// </summary>
    public class WorkflowProfileCommandPreview
    {
        /// <summary>
        /// Check type that this command services.
        /// </summary>
        public CheckRunTypeEnum CheckType { get; set; } = CheckRunTypeEnum.Build;

        /// <summary>
        /// Optional environment name when this is an environment-scoped command.
        /// </summary>
        public string? EnvironmentName { get; set; } = null;

        /// <summary>
        /// Resolved command text.
        /// </summary>
        public string Command { get; set; } = string.Empty;
    }
}
