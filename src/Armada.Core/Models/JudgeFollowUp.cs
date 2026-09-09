namespace Armada.Core.Models
{
    /// <summary>
    /// A durable audit item captured from one completed Judge mission.
    /// </summary>
    public sealed class JudgeFollowUp
    {
        /// <summary>Unique identifier.</summary>
        public string Id
        {
            get => _Id;
            set
            {
                if (String.IsNullOrWhiteSpace(value)) throw new ArgumentNullException(nameof(Id));
                _Id = value;
            }
        }

        /// <summary>Tenant identifier.</summary>
        public string? TenantId { get; set; } = null;

        /// <summary>Owning user identifier.</summary>
        public string? UserId { get; set; } = null;

        /// <summary>Judge mission that produced this item.</summary>
        public string JudgeMissionId
        {
            get => _JudgeMissionId;
            set
            {
                if (String.IsNullOrWhiteSpace(value)) throw new ArgumentNullException(nameof(JudgeMissionId));
                _JudgeMissionId = value;
            }
        }

        /// <summary>Worker mission reviewed by the Judge.</summary>
        public string ReviewedMissionId
        {
            get => _ReviewedMissionId;
            set
            {
                if (String.IsNullOrWhiteSpace(value)) throw new ArgumentNullException(nameof(ReviewedMissionId));
                _ReviewedMissionId = value;
            }
        }

        /// <summary>Voyage identifier.</summary>
        public string? VoyageId { get; set; } = null;

        /// <summary>Vessel identifier.</summary>
        public string? VesselId { get; set; } = null;

        /// <summary>Merge entry associated after durable capture.</summary>
        public string? MergeEntryId { get; set; } = null;

        /// <summary>Judge verdict that caused the audit item.</summary>
        public string JudgeVerdict { get; set; } = "PASS";

        /// <summary>Complete extracted Suggested Follow-ups section.</summary>
        public string? SuggestedFollowUps { get; set; } = null;

        /// <summary>Audit state: Pending, Pass, Concern, or Critical.</summary>
        public string AuditVerdict { get; set; } = "Pending";

        /// <summary>Audit notes.</summary>
        public string? AuditNotes { get; set; } = null;

        /// <summary>Required action for a Critical audit verdict.</summary>
        public string? AuditRecommendedAction { get; set; } = null;

        /// <summary>UTC time when the audit completed.</summary>
        public DateTime? AuditCompletedUtc { get; set; } = null;

        /// <summary>Creation time in UTC.</summary>
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

        /// <summary>Last update time in UTC.</summary>
        public DateTime LastUpdateUtc { get; set; } = DateTime.UtcNow;

        private string _Id = Constants.IdGenerator.GenerateKSortable(Constants.JudgeFollowUpIdPrefix, 24);
        private string _JudgeMissionId = "unknown";
        private string _ReviewedMissionId = "unknown";
    }
}
