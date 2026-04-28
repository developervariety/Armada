namespace Armada.Core.Models
{
    using Armada.Core.Enums;

    /// <summary>
    /// An entry in the merge queue representing a branch to be tested and merged.
    /// </summary>
    public class MergeEntry
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
                if (String.IsNullOrEmpty(value)) throw new ArgumentNullException(nameof(Id));
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
        /// Mission identifier this merge entry belongs to.
        /// </summary>
        public string? MissionId { get; set; } = null;

        /// <summary>
        /// Vessel identifier.
        /// </summary>
        public string? VesselId { get; set; } = null;

        /// <summary>
        /// Branch name to merge.
        /// </summary>
        public string BranchName
        {
            get => _BranchName;
            set
            {
                if (String.IsNullOrEmpty(value)) throw new ArgumentNullException(nameof(BranchName));
                _BranchName = value;
            }
        }

        /// <summary>
        /// Target branch to merge into (e.g., "main").
        /// </summary>
        public string TargetBranch { get; set; } = "main";

        /// <summary>
        /// Current status of this merge entry.
        /// </summary>
        public MergeStatusEnum Status { get; set; } = MergeStatusEnum.Queued;

        /// <summary>
        /// Priority in the queue (lower = higher priority).
        /// </summary>
        public int Priority { get; set; } = 0;

        /// <summary>
        /// Batch identifier when this entry is being tested as part of a batch.
        /// </summary>
        public string? BatchId { get; set; } = null;

        /// <summary>
        /// Test command to run for verification.
        /// </summary>
        public string? TestCommand { get; set; } = null;

        /// <summary>
        /// Test output or error message.
        /// </summary>
        public string? TestOutput { get; set; } = null;

        /// <summary>
        /// Test exit code.
        /// </summary>
        public int? TestExitCode { get; set; } = null;

        /// <summary>
        /// Creation timestamp in UTC.
        /// </summary>
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Last update timestamp in UTC.
        /// </summary>
        public DateTime LastUpdateUtc { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Timestamp when tests started.
        /// </summary>
        public DateTime? TestStartedUtc { get; set; } = null;

        /// <summary>
        /// Timestamp when the entry was landed or failed.
        /// </summary>
        public DateTime? CompletedUtc { get; set; } = null;

        #region Audit

        /// <summary>"Fast" or "Deferred". Null until auto-land safety net evaluates.</summary>
        public string? AuditLane { get; set; }

        /// <summary>Whether convention check passed (no rule violations on '+' lines).</summary>
        public bool? AuditConventionPassed { get; set; }

        /// <summary>JSON list of {rule, line} for any convention violations; null when AuditConventionPassed is true.</summary>
        public string? AuditConventionNotes { get; set; }

        /// <summary>CSV of {"path","content","convention","size"} subset that fired the critical trigger; empty/null when none fired.</summary>
        public string? AuditCriticalTrigger { get; set; }

        /// <summary>1 = entry queued for deep review (calibration or critical trigger); 0/null = no deep review needed.</summary>
        public bool? AuditDeepPicked { get; set; }

        /// <summary>UTC timestamp when audit drainer recorded the deep-review verdict; null while pending.</summary>
        public DateTime? AuditDeepCompletedUtc { get; set; }

        /// <summary>"Pending" | "Pass" | "Concern" | "Critical"; null when AuditDeepPicked is false/null.</summary>
        public string? AuditDeepVerdict { get; set; }

        /// <summary>Subagent's audit notes / rationale; populated alongside AuditDeepVerdict.</summary>
        public string? AuditDeepNotes { get; set; }

        /// <summary>Subagent's recommended action when verdict = Critical; null otherwise.</summary>
        public string? AuditDeepRecommendedAction { get; set; }

        #endregion

        #endregion

        #region Private-Members

        private string _Id = Constants.IdGenerator.GenerateKSortable("mrg_", 24);
        private string _BranchName = "unknown";

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        public MergeEntry()
        {
        }

        /// <summary>
        /// Instantiate with branch name.
        /// </summary>
        /// <param name="branchName">Branch to merge.</param>
        /// <param name="targetBranch">Target branch.</param>
        public MergeEntry(string branchName, string targetBranch = "main")
        {
            BranchName = branchName;
            TargetBranch = targetBranch;
        }

        #endregion
    }
}
