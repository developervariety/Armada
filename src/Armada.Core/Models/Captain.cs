namespace Armada.Core.Models
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json.Serialization;
    using Armada.Core.Enums;

    /// <summary>
    /// A worker agent instance executing missions.
    /// </summary>
    public class Captain
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
        /// Captain name.
        /// </summary>
        public string Name
        {
            get => _Name;
            set
            {
                if (String.IsNullOrEmpty(value)) throw new ArgumentNullException(nameof(Name));
                _Name = value;
            }
        }

        /// <summary>
        /// Agent runtime type.
        /// </summary>
        public AgentRuntimeEnum Runtime { get; set; } = AgentRuntimeEnum.ClaudeCode;

        /// <summary>
        /// Whether this captain's runtime is currently supported by Armada planning sessions.
        /// </summary>
        public bool SupportsPlanningSessions => Runtime != AgentRuntimeEnum.Custom;

        /// <summary>
        /// Reason the captain cannot be used for planning sessions, if any.
        /// </summary>
        public string? PlanningSessionSupportReason => SupportsPlanningSessions
            ? null
            : "Planning sessions currently support only the built-in ClaudeCode, Codex, Gemini, Cursor, and Mux runtimes.";

        /// <summary>
        /// Optional model override for the captain's runtime.
        /// Null means the runtime selects its default model.
        /// </summary>
        public string? Model
        {
            get => _Model;
            set
            {
                if (String.IsNullOrEmpty(value)) _Model = null;
                else _Model = value;
            }
        }

        /// <summary>
        /// User-supplied system instructions for this captain. Injected into every mission's
        /// instructions before vessel context and mission details. Use this to specialize
        /// captain behavior, add guardrails, or provide persistent context.
        /// </summary>
        public string? SystemInstructions { get; set; } = null;

        /// <summary>
        /// JSON array of persona names this captain is allowed to fill.
        /// Null means the captain can take on any persona.
        /// Example: ["Worker", "Judge"]
        /// </summary>
        public string? AllowedPersonas { get; set; } = null;

        /// <summary>
        /// Preferred persona for dispatch routing priority.
        /// The Admiral prefers to assign work matching this persona to this captain.
        /// </summary>
        public string? PreferredPersona { get; set; } = null;

        /// <summary>
        /// Runtime-specific configuration serialized as JSON.
        /// Use this for settings that should not be promoted into generic captain fields.
        /// </summary>
        public string? RuntimeOptionsJson { get; set; } = null;

        /// <summary>
        /// Current state of the captain.
        /// </summary>
        public CaptainStateEnum State { get; set; } = CaptainStateEnum.Idle;

        /// <summary>
        /// Currently assigned mission identifier.
        /// </summary>
        public string? CurrentMissionId { get; set; } = null;

        /// <summary>
        /// Currently assigned dock identifier.
        /// </summary>
        public string? CurrentDockId { get; set; } = null;

        /// <summary>
        /// Operating system process identifier.
        /// </summary>
        public int? ProcessId { get; set; } = null;

        /// <summary>
        /// Number of auto-recovery attempts for the current mission.
        /// </summary>
        public int RecoveryAttempts { get; set; } = 0;

        /// <summary>
        /// Last heartbeat timestamp in UTC.
        /// </summary>
        public DateTime? LastHeartbeatUtc { get; set; } = null;

        /// <summary>
        /// JSON-serialized list of <see cref="Models.SelectedPlaybook"/> entries automatically
        /// merged into every mission this captain runs (Reflections v2-F2). Layered last in the
        /// three-way merge (vessel -&gt; persona -&gt; captain), so captain content wins on collision.
        /// Use <see cref="GetDefaultPlaybooks"/> to obtain a parsed list.
        /// </summary>
        public string? DefaultPlaybooks { get; set; } = null;

        /// <summary>
        /// Per-captain captain-curate trigger threshold (mission-count window since last accepted
        /// captain-curate). Null disables the audit-drain auto-trigger for this captain
        /// (Reflections v2-F2).
        /// </summary>
        public int? CurateThreshold { get; set; } = null;

        /// <summary>
        /// FK reference to the captain-&lt;sanitized-id&gt;-learned playbook. Lazy-created on the
        /// first accepted captain-curate proposal (Reflections v2-F2). Null means no learned
        /// playbook has been accepted for this captain yet.
        /// </summary>
        public string? LearnedPlaybookId { get; set; } = null;

        /// <summary>
        /// Lazy-parses the <see cref="DefaultPlaybooks"/> JSON string. Returns an empty list when unset or malformed.
        /// </summary>
        /// <returns>List of <see cref="Models.SelectedPlaybook"/> entries.</returns>
        public List<SelectedPlaybook> GetDefaultPlaybooks()
        {
            if (String.IsNullOrWhiteSpace(DefaultPlaybooks)) return new List<SelectedPlaybook>();
            try
            {
                List<SelectedPlaybook>? list = System.Text.Json.JsonSerializer.Deserialize<List<SelectedPlaybook>>(
                    DefaultPlaybooks,
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                return list ?? new List<SelectedPlaybook>();
            }
            catch
            {
                return new List<SelectedPlaybook>();
            }
        }

        /// <summary>
        /// Creation timestamp in UTC.
        /// </summary>
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Last update timestamp in UTC.
        /// </summary>
        public DateTime LastUpdateUtc { get; set; } = DateTime.UtcNow;

        #endregion

        #region Private-Members

        private string _Id = Constants.IdGenerator.GenerateKSortable(Constants.CaptainIdPrefix, 24);
        private string _Name = "Captain";
        private string? _Model = null;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        public Captain()
        {
        }

        /// <summary>
        /// Instantiate with name and runtime.
        /// </summary>
        /// <param name="name">Captain name.</param>
        /// <param name="runtime">Agent runtime type.</param>
        public Captain(string name, AgentRuntimeEnum runtime = AgentRuntimeEnum.ClaudeCode)
        {
            Name = name;
            Runtime = runtime;
        }

        #endregion
    }
}
