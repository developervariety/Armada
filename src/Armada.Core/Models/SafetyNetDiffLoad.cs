namespace Armada.Core.Models
{
    using Armada.Core.Enums;

    /// <summary>
    /// The diff the landing-drain safety net measures a branch with, and how it was obtained.
    /// </summary>
    public class SafetyNetDiffLoad
    {
        #region Public-Members

        /// <summary>
        /// How the load ended.
        /// </summary>
        public SafetyNetDiffOutcomeEnum Outcome { get; set; } = SafetyNetDiffOutcomeEnum.Loaded;

        /// <summary>
        /// The unified diff; null for every outcome other than <see cref="SafetyNetDiffOutcomeEnum.Loaded"/>.
        /// </summary>
        public string? Diff { get; set; } = null;

        /// <summary>
        /// Why the diff is missing, when it is.
        /// </summary>
        public string? Detail { get; set; } = null;

        #endregion
    }
}
