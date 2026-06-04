namespace Armada.Core.Models
{
    using Armada.Core.Enums;

    /// <summary>
    /// Durable landing state associated with one merge queue entry.
    /// </summary>
    public class LandingJob
    {
        #region Public-Members

        /// <summary>
        /// Unique identifier.
        /// </summary>
        public string Id
        {
            get => _Id;
            set
            {
                if (String.IsNullOrWhiteSpace(value)) throw new ArgumentNullException(nameof(Id));
                _Id = value;
            }
        }

        /// <summary>
        /// Tenant identifier.
        /// </summary>
        public string? TenantId { get; set; } = null;

        /// <summary>
        /// Owning user identifier.
        /// </summary>
        public string? UserId { get; set; } = null;

        /// <summary>
        /// Merge entry this job drives.
        /// </summary>
        public string MergeEntryId
        {
            get => _MergeEntryId;
            set
            {
                if (String.IsNullOrWhiteSpace(value)) throw new ArgumentNullException(nameof(MergeEntryId));
                _MergeEntryId = value;
            }
        }

        /// <summary>
        /// Mission identifier associated with the merge entry.
        /// </summary>
        public string? MissionId { get; set; } = null;

        /// <summary>
        /// Vessel identifier associated with the merge entry.
        /// </summary>
        public string? VesselId { get; set; } = null;

        /// <summary>
        /// Branch being landed.
        /// </summary>
        public string BranchName
        {
            get => _BranchName;
            set
            {
                if (String.IsNullOrWhiteSpace(value)) throw new ArgumentNullException(nameof(BranchName));
                _BranchName = value;
            }
        }

        /// <summary>
        /// Target branch.
        /// </summary>
        public string TargetBranch { get; set; } = "main";

        /// <summary>
        /// Current durable landing state.
        /// </summary>
        public LandingJobStateEnum State { get; set; } = LandingJobStateEnum.Queued;

        /// <summary>
        /// Retry count reserved for bounded landing retry orchestration.
        /// </summary>
        public int RetryCount { get; set; } = 0;

        /// <summary>
        /// Creation timestamp in UTC.
        /// </summary>
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Last update timestamp in UTC.
        /// </summary>
        public DateTime LastUpdateUtc { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Timestamp when landing first left the queue.
        /// </summary>
        public DateTime? StartedUtc { get; set; } = null;

        /// <summary>
        /// Timestamp when landing reached a terminal state.
        /// </summary>
        public DateTime? CompletedUtc { get; set; } = null;

        /// <summary>
        /// Last terminal or recoverable error.
        /// </summary>
        public string? LastError { get; set; } = null;

        #endregion

        #region Private-Members

        private string _Id = Constants.IdGenerator.GenerateKSortable("ljb_", 24);
        private string _MergeEntryId = "unknown";
        private string _BranchName = "unknown";

        #endregion
    }
}
