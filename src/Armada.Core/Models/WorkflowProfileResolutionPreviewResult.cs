namespace Armada.Core.Models
{
    using System.Collections.Generic;
    using Armada.Core.Enums;

    /// <summary>
    /// Fully resolved workflow-profile preview for a target vessel.
    /// </summary>
    public class WorkflowProfileResolutionPreviewResult
    {
        /// <summary>
        /// Profile that Armada resolved for the target vessel.
        /// </summary>
        public WorkflowProfile? ResolvedProfile { get; set; } = null;

        /// <summary>
        /// How the resolved profile was selected.
        /// </summary>
        public WorkflowProfileResolutionModeEnum ResolutionMode { get; set; } = WorkflowProfileResolutionModeEnum.Global;

        /// <summary>
        /// Available check types exposed by the resolved profile.
        /// </summary>
        public List<string> AvailableCheckTypes { get; set; } = new List<string>();

        /// <summary>
        /// Fully resolved command previews across base and environment-specific commands.
        /// </summary>
        public List<WorkflowProfileCommandPreview> CommandPreviews { get; set; } = new List<WorkflowProfileCommandPreview>();
    }
}
