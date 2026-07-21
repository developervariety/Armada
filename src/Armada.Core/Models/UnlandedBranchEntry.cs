namespace Armada.Core.Models
{
    /// <summary>
    /// A single Armada mission branch that has not been merged into its vessel's default branch.
    /// </summary>
    public class UnlandedBranchEntry
    {
        #region Public-Members

        /// <summary>
        /// Branch name as it exists in the vessel's repository.
        /// </summary>
        public string BranchName { get; set; } = "";

        /// <summary>
        /// Mission identifier parsed from the branch name, or null when the branch does not follow
        /// the mission naming convention (for example a merge-queue integration branch).
        /// </summary>
        public string? MissionId { get; set; } = null;

        /// <summary>
        /// Status of the referenced mission, or null when no mission record resolves. A null status
        /// on a branch that does carry a mission id means the record was deleted or purged, which is
        /// itself a signal that the branch is abandoned.
        /// </summary>
        public string? MissionStatus { get; set; } = null;

        #endregion
    }
}
