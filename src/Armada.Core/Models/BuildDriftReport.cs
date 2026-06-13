namespace Armada.Core.Models
{
    /// <summary>
    /// Describes the drift state between the running server build and the latest landed commit.
    /// </summary>
    public class BuildDriftReport
    {
        #region Public-Members

        /// <summary>
        /// The git commit SHA the running server binary was built from, or null if not embedded.
        /// </summary>
        public string? RunningCommit { get; set; } = null;

        /// <summary>
        /// The git commit SHA of the latest landed main branch commit, or null if unavailable.
        /// </summary>
        public string? LandedCommit { get; set; } = null;

        /// <summary>
        /// The number of commits the running build is behind the landed commit.
        /// </summary>
        public int BehindBy { get; set; } = 0;

        /// <summary>
        /// Indicates whether the running build differs from the landed commit.
        /// </summary>
        public bool IsDrifted { get; set; } = false;

        /// <summary>
        /// Human-readable warning when the build is drifted, or null when up to date.
        /// </summary>
        public string? Warning { get; set; } = null;

        #endregion
    }
}
