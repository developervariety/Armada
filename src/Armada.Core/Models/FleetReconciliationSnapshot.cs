namespace Armada.Core.Models
{
    using Armada.Core.Enums;

    /// <summary>
    /// Authoritative fleet state used to reconcile a WebSocket client after a connection gap.
    /// </summary>
    public sealed class FleetReconciliationSnapshot
    {
        /// <summary>UTC time when the snapshot was completed.</summary>
        public DateTime GeneratedUtc { get; set; } = DateTime.UtcNow;
        /// <summary>Requested voyage scope, or null for active global state.</summary>
        public string? VoyageId { get; set; }
        /// <summary>Voyages included in the snapshot.</summary>
        public List<FleetSnapshotVoyage> Voyages { get; set; } = new List<FleetSnapshotVoyage>();
        /// <summary>Lightweight missions linked to the included voyages.</summary>
        public List<MissionSummary> Missions { get; set; } = new List<MissionSummary>();
        /// <summary>Captains included in the snapshot.</summary>
        public List<FleetSnapshotCaptain> Captains { get; set; } = new List<FleetSnapshotCaptain>();
        /// <summary>Check runs linked to the included voyages or missions.</summary>
        public List<FleetSnapshotCheckRun> CheckRuns { get; set; } = new List<FleetSnapshotCheckRun>();
    }

    /// <summary>
    /// Lightweight voyage state for fleet reconciliation.
    /// </summary>
    public sealed class FleetSnapshotVoyage
    {
        /// <summary>Voyage identifier.</summary>
        public string Id { get; set; } = string.Empty;
        /// <summary>Voyage title.</summary>
        public string Title { get; set; } = string.Empty;
        /// <summary>Current voyage status.</summary>
        public VoyageStatusEnum Status { get; set; }
        /// <summary>Creation time in UTC.</summary>
        public DateTime CreatedUtc { get; set; }
        /// <summary>Completion time in UTC, when complete.</summary>
        public DateTime? CompletedUtc { get; set; }
        /// <summary>Last update time in UTC.</summary>
        public DateTime LastUpdateUtc { get; set; }
    }

    /// <summary>
    /// Non-secret captain state for fleet reconciliation.
    /// </summary>
    public sealed class FleetSnapshotCaptain
    {
        /// <summary>Captain identifier.</summary>
        public string Id { get; set; } = string.Empty;
        /// <summary>Captain name.</summary>
        public string Name { get; set; } = string.Empty;
        /// <summary>Agent runtime used by the captain.</summary>
        public AgentRuntimeEnum Runtime { get; set; }
        /// <summary>Current captain state.</summary>
        public CaptainStateEnum State { get; set; }
        /// <summary>Current mission identifier, when assigned.</summary>
        public string? CurrentMissionId { get; set; }
        /// <summary>Current dock identifier, when assigned.</summary>
        public string? CurrentDockId { get; set; }
        /// <summary>Current operating-system process identifier, when running.</summary>
        public int? ProcessId { get; set; }
        /// <summary>Recovery attempts for the current mission.</summary>
        public int RecoveryAttempts { get; set; }
        /// <summary>Last output heartbeat time in UTC.</summary>
        public DateTime? LastHeartbeatUtc { get; set; }
        /// <summary>Last observed process-alive time in UTC.</summary>
        public DateTime? LastProcessAliveUtc { get; set; }
    }

    /// <summary>
    /// Lightweight Check state for fleet reconciliation.
    /// </summary>
    public sealed class FleetSnapshotCheckRun
    {
        /// <summary>Check-run identifier.</summary>
        public string Id { get; set; } = string.Empty;
        /// <summary>Linked voyage identifier, when present.</summary>
        public string? VoyageId { get; set; }
        /// <summary>Linked mission identifier, when present.</summary>
        public string? MissionId { get; set; }
        /// <summary>Linked vessel identifier, when present.</summary>
        public string? VesselId { get; set; }
        /// <summary>Optional display label.</summary>
        public string? Label { get; set; }
        /// <summary>Type of Check.</summary>
        public CheckRunTypeEnum Type { get; set; }
        /// <summary>Source of the Check result.</summary>
        public CheckRunSourceEnum Source { get; set; }
        /// <summary>Current Check status.</summary>
        public CheckRunStatusEnum Status { get; set; }
        /// <summary>Commit hash verified by the Check, when present.</summary>
        public string? CommitHash { get; set; }
        /// <summary>Time spent waiting for a command slot, in milliseconds.</summary>
        public long? QueueDurationMs { get; set; }
        /// <summary>Check execution duration in milliseconds.</summary>
        public long? DurationMs { get; set; }
        /// <summary>Creation time in UTC.</summary>
        public DateTime CreatedUtc { get; set; }
        /// <summary>Start time in UTC, when started.</summary>
        public DateTime? StartedUtc { get; set; }
        /// <summary>Completion time in UTC, when complete.</summary>
        public DateTime? CompletedUtc { get; set; }
        /// <summary>Last update time in UTC.</summary>
        public DateTime LastUpdateUtc { get; set; }
    }
}
