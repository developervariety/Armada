namespace Armada.Core.Models
{
    using System.Collections.Generic;

    /// <summary>
    /// Validation and preview output for a workflow profile.
    /// </summary>
    public class WorkflowProfileValidationResult
    {
        /// <summary>
        /// Whether the profile is valid.
        /// </summary>
        public bool IsValid { get; set; } = true;

        /// <summary>
        /// Validation errors.
        /// </summary>
        public List<string> Errors { get; set; } = new List<string>();

        /// <summary>
        /// Non-fatal warnings.
        /// </summary>
        public List<string> Warnings { get; set; } = new List<string>();

        /// <summary>
        /// Check types that can currently be resolved from the profile.
        /// </summary>
        public List<string> AvailableCheckTypes { get; set; } = new List<string>();
    }
}
