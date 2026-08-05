namespace Armada.Core.Models
{
    /// <summary>
    /// One vessel whose code index is behind the current default-branch commit.
    /// </summary>
    public class CodeIndexStaleVessel
    {
        #region Public-Members

        /// <summary>
        /// Vessel identifier.
        /// </summary>
        public string VesselId { get; set; } = String.Empty;

        /// <summary>
        /// Vessel display name.
        /// </summary>
        public string VesselName { get; set; } = String.Empty;

        /// <summary>
        /// Commit the index was built from.
        /// </summary>
        public string IndexedCommitSha { get; set; } = String.Empty;

        /// <summary>
        /// Current default-branch commit in the vessel's repository.
        /// </summary>
        public string CurrentCommitSha { get; set; } = String.Empty;

        #endregion
    }
}
