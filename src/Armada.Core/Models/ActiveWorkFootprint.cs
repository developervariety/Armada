namespace Armada.Core.Models
{
    using System.Collections.Generic;
    using Armada.Core.Enums;

    /// <summary>
    /// One mission's vessel footprint inside active work, as fleet-capacity admission counts it.
    /// A mission in an Open or InProgress voyage belongs to that voyage's work unit whatever its own
    /// status; a mission with no voyage is its own work unit while its status is active.
    /// </summary>
    public sealed class ActiveWorkFootprint
    {
        #region Public-Members

        /// <summary>
        /// Voyage statuses whose missions occupy fleet capacity.
        /// </summary>
        public static readonly IReadOnlyList<VoyageStatusEnum> ActiveVoyageStatuses = new VoyageStatusEnum[]
        {
            VoyageStatusEnum.Open,
            VoyageStatusEnum.InProgress
        };

        /// <summary>
        /// Mission statuses in which a mission without a voyage occupies fleet capacity.
        /// </summary>
        public static readonly IReadOnlyList<MissionStatusEnum> ActiveStandaloneMissionStatuses = new MissionStatusEnum[]
        {
            MissionStatusEnum.Pending,
            MissionStatusEnum.Assigned,
            MissionStatusEnum.InProgress,
            MissionStatusEnum.WorkProduced,
            MissionStatusEnum.PullRequestOpen,
            MissionStatusEnum.Testing,
            MissionStatusEnum.Review
        };

        /// <summary>
        /// Mission identifier.
        /// </summary>
        public string MissionId { get; set; } = "";

        /// <summary>
        /// Voyage identifier, or null for a mission without a voyage.
        /// </summary>
        public string? VoyageId { get; set; } = null;

        /// <summary>
        /// Vessel the mission works in.
        /// </summary>
        public string VesselId { get; set; } = "";

        #endregion
    }
}
