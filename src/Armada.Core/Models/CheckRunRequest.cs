namespace Armada.Core.Models
{
    using Armada.Core.Enums;

    /// <summary>
    /// Request to execute a structured check run.
    /// </summary>
    public class CheckRunRequest
    {
        /// <summary>
        /// Vessel against which the check should run.
        /// </summary>
        public string VesselId { get; set; } = string.Empty;

        /// <summary>
        /// Optional workflow profile override.
        /// </summary>
        public string? WorkflowProfileId { get; set; } = null;

        /// <summary>
        /// Optional mission link.
        /// </summary>
        public string? MissionId { get; set; } = null;

        /// <summary>
        /// Optional voyage link.
        /// </summary>
        public string? VoyageId { get; set; } = null;

        /// <summary>
        /// Check type to execute.
        /// </summary>
        public CheckRunTypeEnum Type { get; set; } = CheckRunTypeEnum.Build;

        /// <summary>
        /// Optional environment target.
        /// </summary>
        public string? EnvironmentName { get; set; } = null;

        /// <summary>
        /// Optional display label override.
        /// </summary>
        public string? Label { get; set; } = null;

        /// <summary>
        /// Optional branch name association.
        /// </summary>
        public string? BranchName { get; set; } = null;

        /// <summary>
        /// Optional commit-hash association.
        /// </summary>
        public string? CommitHash { get; set; } = null;

        /// <summary>
        /// Optional raw command override.
        /// </summary>
        public string? CommandOverride { get; set; } = null;
    }
}
