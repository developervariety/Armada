namespace Armada.Server
{
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Runtime.InteropServices;
    using System.Text.Json;
    using SyslogLogging;
    using Armada.Core;
    using ArmadaConstants = Armada.Core.Constants;
    using Armada.Core.Database;
    using Armada.Core.Database.Sqlite;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Runtimes;
    using Armada.Server.Mcp;
    using Armada.Server.WebSocket;

    /// <summary>
    /// Request model for partial update of server settings.
    /// </summary>
    public class SettingsUpdateRequest
    {
        /// <summary>
        /// Admiral REST API port (1-65535).
        /// </summary>
        public int? AdmiralPort { get; set; }

        /// <summary>
        /// MCP server port (1-65535).
        /// </summary>
        public int? McpPort { get; set; }

        /// <summary>
        /// Maximum captains allowed (0 = unlimited).
        /// </summary>
        public int? MaxCaptains { get; set; }

        /// <summary>
        /// Maximum concurrent captain workloads across all vessels (0 = unlimited).
        /// Caps combined captain + compiler memory pressure to prevent host OOM.
        /// </summary>
        public int? MaxConcurrentCaptainWorkloads { get; set; }

        /// <summary>
        /// Heartbeat check interval in seconds (>= 5).
        /// </summary>
        public int? HeartbeatIntervalSeconds { get; set; }

        /// <summary>
        /// Stall detection threshold in minutes (>= 1).
        /// </summary>
        public int? StallThresholdMinutes { get; set; }

        /// <summary>
        /// Idle captain timeout in seconds (0 = disabled).
        /// </summary>
        public int? IdleCaptainTimeoutSeconds { get; set; }

        /// <summary>
        /// Idle planning-session timeout in minutes (0 = disabled).
        /// </summary>
        public int? PlanningSessionInactivityTimeoutMinutes { get; set; }

        /// <summary>
        /// Abandonment timeout in minutes for planning sessions without a running process (0 = disabled).
        /// </summary>
        public int? PlanningSessionAbandonmentTimeoutMinutes { get; set; }

        /// <summary>
        /// Retention period in days for stopped or failed planning sessions (0 = disabled).
        /// </summary>
        public int? PlanningSessionRetentionDays { get; set; }

        /// <summary>
        /// Whether to auto-create pull requests on mission completion.
        /// </summary>
        public bool? AutoCreatePr { get; set; }

        /// <summary>
        /// Optional remote-control tunnel settings update.
        /// When supplied, replaces the full remoteControl settings object.
        /// </summary>
        public RemoteControlSettings? RemoteControl { get; set; }

        /// <summary>
        /// Optional resource-pressure admission update. Every member is individually
        /// optional, so a caller may raise maxConcurrentBuilds alone without resetting
        /// the memory floor or the OOM cooldown.
        /// </summary>
        public ResourcePressureAdmissionUpdate? ResourcePressureAdmission { get; set; }

        /// <summary>
        /// Optional model-tier routing update. Every member is individually optional,
        /// so a caller may reorder one tier's preference list without rewriting the
        /// tier membership lists or the capability profiles.
        /// </summary>
        public ModelTierUpdate? ModelTier { get; set; }
    }

    /// <summary>
    /// Partial update for the resource-pressure admission policy. A null member
    /// leaves the current value in place.
    /// </summary>
    public class ResourcePressureAdmissionUpdate
    {
        /// <summary>
        /// Whether resource-pressure admission gating is active.
        /// </summary>
        public bool? Enabled { get; set; }

        /// <summary>
        /// Minimum available memory in megabytes required to admit a captain launch.
        /// Zero disables the memory gate.
        /// </summary>
        public int? MinAvailableMemoryMb { get; set; }

        /// <summary>
        /// Maximum concurrent captain/build workloads before launches are deferred.
        /// Zero means unlimited. Clamped to [0, 1000].
        /// </summary>
        public int? MaxConcurrentBuilds { get; set; }

        /// <summary>
        /// Seconds that captain launches are deferred after a kernel OOM classification.
        /// Clamped to [1, 7200].
        /// </summary>
        public int? OomCooldownSeconds { get; set; }

        /// <summary>
        /// Apply the supplied members to the live settings object, in place.
        /// </summary>
        /// <param name="target">Live settings object to mutate.</param>
        public void ApplyTo(ResourcePressureAdmissionSettings target)
        {
            if (target == null) return;
            if (Enabled.HasValue) target.Enabled = Enabled.Value;
            if (MinAvailableMemoryMb.HasValue) target.MinAvailableMemoryMb = MinAvailableMemoryMb.Value;
            if (MaxConcurrentBuilds.HasValue) target.MaxConcurrentBuilds = MaxConcurrentBuilds.Value;
            if (OomCooldownSeconds.HasValue) target.OomCooldownSeconds = OomCooldownSeconds.Value;
        }
    }

    /// <summary>
    /// Partial update for model-tier routing. A null member leaves the current
    /// value in place; a supplied collection replaces that collection outright.
    /// </summary>
    public class ModelTierUpdate
    {
        /// <summary>
        /// Persona names routed only to high-tier captains.
        /// </summary>
        public List<string>? SpecialistPersonas { get; set; }

        /// <summary>
        /// Idle high-tier captain slots held in reserve for specialist missions.
        /// Clamped to [0, 10]. Zero disables the reservation.
        /// </summary>
        public int? ReservedHighTierSlots { get; set; }

        /// <summary>
        /// Per-tier model preference order. The selector picks the first listed model
        /// that has an idle, persona-eligible captain.
        /// </summary>
        public Dictionary<string, List<string>>? WithinTierPreferenceOrder { get; set; }

        /// <summary>
        /// Per-model capability profiles keyed by concrete model name.
        /// </summary>
        public Dictionary<string, ModelCapabilityProfile>? ModelCapabilityProfiles { get; set; }

        /// <summary>
        /// Maps capability hint names to the profile dimension they optimize.
        /// </summary>
        public Dictionary<string, string>? CapabilityHintDimensionMap { get; set; }

        /// <summary>
        /// Low-complexity model names.
        /// </summary>
        public List<string>? LowTierModels { get; set; }

        /// <summary>
        /// Mid-complexity model names.
        /// </summary>
        public List<string>? MidTierModels { get; set; }

        /// <summary>
        /// High-complexity model names.
        /// </summary>
        public List<string>? HighTierModels { get; set; }

        /// <summary>
        /// Apply the supplied members to the live settings object, in place.
        /// </summary>
        /// <param name="target">Live settings object to mutate.</param>
        public void ApplyTo(ModelTierSettings target)
        {
            if (target == null) return;
            if (SpecialistPersonas != null) target.SpecialistPersonas = SpecialistPersonas;
            if (ReservedHighTierSlots.HasValue) target.ReservedHighTierSlots = ReservedHighTierSlots.Value;
            if (WithinTierPreferenceOrder != null) target.WithinTierPreferenceOrder = WithinTierPreferenceOrder;
            if (ModelCapabilityProfiles != null) target.ModelCapabilityProfiles = ModelCapabilityProfiles;
            if (CapabilityHintDimensionMap != null) target.CapabilityHintDimensionMap = CapabilityHintDimensionMap;
            if (LowTierModels != null) target.LowTierModels = LowTierModels;
            if (MidTierModels != null) target.MidTierModels = MidTierModels;
            if (HighTierModels != null) target.HighTierModels = HighTierModels;
        }
    }
}
