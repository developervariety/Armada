namespace Armada.Core.Models
{
    using Armada.Core.Enums;

    /// <summary>
    /// Resolved workflow profile plus the source mode that produced it.
    /// </summary>
    public class WorkflowProfileResolutionResult
    {
        /// <summary>
        /// Resolved profile, if any.
        /// </summary>
        public WorkflowProfile? Profile { get; set; } = null;

        /// <summary>
        /// Resolution source mode.
        /// </summary>
        public WorkflowProfileResolutionModeEnum Mode { get; set; } = WorkflowProfileResolutionModeEnum.Global;
    }
}
