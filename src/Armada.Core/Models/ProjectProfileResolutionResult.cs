namespace Armada.Core.Models
{
    using Armada.Core.Enums;

    /// <summary>
    /// The outcome of resolving a project profile for a vessel: the selected profile (if any) and the
    /// mode by which it was selected.
    /// </summary>
    public class ProjectProfileResolutionResult
    {
        /// <summary>
        /// The resolved profile, or null when no matching profile exists.
        /// </summary>
        public ProjectProfile? Profile { get; set; } = null;

        /// <summary>
        /// How the profile was selected.
        /// </summary>
        public ProjectProfileResolutionModeEnum Mode { get; set; } = ProjectProfileResolutionModeEnum.None;
    }
}
