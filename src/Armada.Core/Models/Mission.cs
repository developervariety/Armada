namespace Armada.Core.Models
{
    using System.Collections.Generic;
    using System.Text.Json.Serialization;
    using Armada.Core.Enums;

    /// <summary>
    /// An atomic unit of work assigned to a captain.
    /// </summary>
    public class Mission
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
        /// Parent voyage identifier.
        /// </summary>
        public string? VoyageId { get; set; } = null;

        /// <summary>
        /// Target vessel identifier.
        /// </summary>
        public string? VesselId { get; set; } = null;

        /// <summary>
        /// Assigned captain identifier.
        /// </summary>
        public string? CaptainId { get; set; } = null;

        /// <summary>
        /// Mission title.
        /// </summary>
        public string Title
        {
            get => _Title;
            set
            {
                if (String.IsNullOrEmpty(value)) throw new ArgumentNullException(nameof(Title));
                _Title = value;
            }
        }

        /// <summary>
        /// Detailed mission description and instructions.
        /// </summary>
        public string? Description { get; set; } = null;

        /// <summary>
        /// Current mission status.
        /// </summary>
        public MissionStatusEnum Status { get; set; } = MissionStatusEnum.Pending;

        /// <summary>
        /// Mission priority (lower is higher priority).
        /// </summary>
        public int Priority { get; set; } = 100;

        /// <summary>
        /// Parent mission identifier for sub-tasks.
        /// </summary>
        public string? ParentMissionId { get; set; } = null;

        /// <summary>
        /// Git branch name for this mission's work.
        /// </summary>
        public string? BranchName { get; set; } = null;

        /// <summary>
        /// Assigned dock identifier for this mission's worktree.
        /// </summary>
        public string? DockId { get; set; } = null;

        /// <summary>
        /// Operating system process identifier for the agent working this mission.
        /// </summary>
        public int? ProcessId { get; set; } = null;

        /// <summary>
        /// Pull request URL if created.
        /// </summary>
        public string? PrUrl { get; set; } = null;

        /// <summary>
        /// Git commit hash (HEAD) captured when the mission completed.
        /// </summary>
        public string? CommitHash { get; set; } = null;

        /// <summary>
        /// Saved git diff snapshot captured at mission completion, before worktree reclamation.
        /// </summary>
        public string? DiffSnapshot { get; set; } = null;

        /// <summary>
        /// Accumulated agent stdout output captured during mission execution.
        /// Used by architect missions for [ARMADA:MISSION] marker parsing
        /// and by pipeline handoff to pass context to the next stage.
        /// </summary>
        public string? AgentOutput { get; set; } = null;

        /// <summary>
        /// Persona assigned to this mission (e.g. "Worker", "Architect", "Judge").
        /// Null defaults to "Worker" for backward compatibility.
        /// </summary>
        public string? Persona { get; set; } = null;

        /// <summary>
        /// Mission ID that this mission depends on.
        /// When set, this mission cannot be assigned until the dependency completes successfully.
        /// Used by pipelines to chain persona stages.
        /// </summary>
        public string? DependsOnMissionId { get; set; } = null;

        /// <summary>
        /// Optional captain identifier this mission must be assigned to.
        /// When set, dispatch will only assign this mission to the captain with this ID
        /// when that captain is idle; if busy, the mission stays pending until the next
        /// dispatch tick. Hard pin -- never falls back to other captains.
        /// </summary>
        public string? PreferredCaptainId { get; set; } = null;

        /// <summary>
        /// Optional captain Model filter for mission assignment. When set, dispatch
        /// considers only idle captains whose <see cref="Captain.Model"/> matches this
        /// value (case-insensitive) before applying persona-preference logic.
        /// </summary>
        public string? PreferredModel { get; set; } = null;

        /// <summary>
        /// Human-readable reason for failure or landing failure.
        /// Set when a mission transitions to Failed or LandingFailed status.
        /// </summary>
        public string? FailureReason { get; set; } = null;

        /// <summary>
        /// Selected playbooks supplied when creating or updating the mission.
        /// This is request metadata and is not stored directly on the mission row.
        /// </summary>
        public List<SelectedPlaybook> SelectedPlaybooks { get; set; } = new List<SelectedPlaybook>();

        /// <summary>
        /// Immutable snapshots of playbooks used for this mission.
        /// </summary>
        public List<MissionPlaybookSnapshot> PlaybookSnapshots { get; set; } = new List<MissionPlaybookSnapshot>();

        /// <summary>
        /// Optional list of file-copy operations performed by the Admiral after a
        /// dock worktree is created and before the captain process is spawned. Each
        /// entry copies a file from an absolute path on the Admiral host into a
        /// relative path within the dock worktree. Persisted as a JSON-serialized
        /// list on the missions row.
        /// </summary>
        public List<PrestagedFile>? PrestagedFiles { get; set; } = null;

        /// <summary>
        /// Creation timestamp in UTC.
        /// </summary>
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Timestamp when work started in UTC.
        /// </summary>
        public DateTime? StartedUtc
        {
            get => _StartedUtc;
            set
            {
                _StartedUtc = value;
                RecalculateTotalRuntimeMs();
            }
        }

        /// <summary>
        /// Timestamp when work completed in UTC.
        /// </summary>
        public DateTime? CompletedUtc
        {
            get => _CompletedUtc;
            set
            {
                _CompletedUtc = value;
                RecalculateTotalRuntimeMs();
            }
        }

        /// <summary>
        /// Total runtime in milliseconds, computed from StartedUtc and CompletedUtc.
        /// </summary>
        public long? TotalRuntimeMs
        {
            get => _TotalRuntimeMs;
            set => _TotalRuntimeMs = value;
        }

        /// <summary>
        /// Last update timestamp in UTC.
        /// </summary>
        public DateTime LastUpdateUtc { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Number of times the auto-recovery loop has dispatched a
        /// redispatch or rebase-captain action for this mission. Hard-capped
        /// at 2 by the recovery router; further failures route to surface
        /// (PR fallback). Defaults to 0; never decremented.
        /// </summary>
        public int RecoveryAttempts { get; set; } = 0;

        /// <summary>
        /// UTC timestamp of the most recent recovery action taken for this
        /// mission. Null until auto-recovery has fired at least once.
        /// </summary>
        public DateTime? LastRecoveryActionUtc { get; set; } = null;

        #endregion

        #region Private-Members

        private string _Id = Constants.IdGenerator.GenerateKSortable(Constants.MissionIdPrefix, 24);
        private string _Title = "New Mission";
        private DateTime? _StartedUtc = null;
        private DateTime? _CompletedUtc = null;
        private long? _TotalRuntimeMs = null;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        public Mission()
        {
        }

        /// <summary>
        /// Instantiate with title.
        /// </summary>
        /// <param name="title">Mission title.</param>
        /// <param name="description">Mission description.</param>
        public Mission(string title, string? description = null)
        {
            Title = title;
            Description = description;
        }

        #endregion

        #region Private-Methods

        private void RecalculateTotalRuntimeMs()
        {
            if (_StartedUtc.HasValue && _CompletedUtc.HasValue)
            {
                double totalMilliseconds = (_CompletedUtc.Value - _StartedUtc.Value).TotalMilliseconds;
                _TotalRuntimeMs = totalMilliseconds >= 0 ? Convert.ToInt64(totalMilliseconds) : null;
            }
            else
            {
                _TotalRuntimeMs = null;
            }
        }

        #endregion
    }
}
