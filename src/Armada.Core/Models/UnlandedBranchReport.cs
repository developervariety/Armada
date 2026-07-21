namespace Armada.Core.Models
{
    using System.Collections.Generic;

    /// <summary>
    /// Per-vessel summary of Armada mission branches that exist in the repository but have not been
    /// merged into the vessel's default branch. Makes stranded work visible as a standing number
    /// rather than something discovered by accident.
    /// </summary>
    public class UnlandedBranchReport
    {
        #region Public-Members

        /// <summary>
        /// Vessel identifier.
        /// </summary>
        public string VesselId { get; set; } = "";

        /// <summary>
        /// Vessel display name.
        /// </summary>
        public string VesselName { get; set; } = "";

        /// <summary>
        /// Branch the mission branches are measured against.
        /// </summary>
        public string DefaultBranch { get; set; } = "";

        /// <summary>
        /// Total Armada mission branches found in the repository.
        /// </summary>
        public int MissionBranchCount { get; set; } = 0;

        /// <summary>
        /// Number of those branches not merged into <see cref="DefaultBranch"/>.
        /// </summary>
        public int UnlandedCount { get; set; } = 0;

        /// <summary>
        /// The unlanded branches themselves.
        /// </summary>
        public List<UnlandedBranchEntry> Unlanded { get; set; } = new List<UnlandedBranchEntry>();

        /// <summary>
        /// Non-null when the vessel could not be measured, for example when it has no local
        /// repository path. The vessel is reported with the reason rather than skipped silently,
        /// so an unmeasurable vessel cannot masquerade as a clean one.
        /// </summary>
        public string? Error { get; set; } = null;

        #endregion
    }
}
