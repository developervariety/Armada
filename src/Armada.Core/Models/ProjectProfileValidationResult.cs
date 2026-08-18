namespace Armada.Core.Models
{
    using System.Collections.Generic;

    /// <summary>
    /// Validation outcome for a project profile.
    /// </summary>
    public class ProjectProfileValidationResult
    {
        /// <summary>
        /// Whether the profile is valid (no errors).
        /// </summary>
        public bool IsValid { get; set; } = false;

        /// <summary>
        /// Blocking validation errors.
        /// </summary>
        public List<string> Errors { get; set; } = new List<string>();

        /// <summary>
        /// Non-blocking warnings.
        /// </summary>
        public List<string> Warnings { get; set; } = new List<string>();
    }
}
