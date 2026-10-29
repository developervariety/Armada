namespace Armada.Core.Models
{
    using System;
    using Armada.Core.Enums;

    /// <summary>
    /// Who ran one captain process attempt at a mission, when it ran, and how it ended. One row is written when the
    /// process launches and closed when its exit is observed. The row is deleted with its mission.
    /// </summary>
    public sealed class MissionAttemptProvenance
    {
        /// <summary>Maximum stored length of a reason code.</summary>
        public const int MaximumReasonCodeLength = 96;

        /// <summary>Unique identifier.</summary>
        public string Id { get; set; } = Constants.IdGenerator.GenerateKSortable(Constants.MissionAttemptProvenanceIdPrefix, 24);

        /// <summary>Tenant identifier.</summary>
        public string? TenantId { get; set; } = null;

        /// <summary>User identifier.</summary>
        public string? UserId { get; set; } = null;

        /// <summary>Mission the attempt worked.</summary>
        public string MissionId { get; set; } = String.Empty;

        /// <summary>Voyage of the mission when the attempt launched.</summary>
        public string? VoyageId { get; set; } = null;

        /// <summary>Vessel of the mission when the attempt launched.</summary>
        public string? VesselId { get; set; } = null;

        /// <summary>Captain that ran the attempt.</summary>
        public string? CaptainId { get; set; } = null;

        /// <summary>Persona the mission ran as.</summary>
        public string? Persona { get; set; } = null;

        /// <summary>Agent runtime the captain used.</summary>
        public string? Runtime { get; set; } = null;

        /// <summary>Model the captain asked the runtime for; null when the runtime chose its default.</summary>
        public string? Model { get; set; } = null;

        /// <summary>
        /// Version the runtime binary reported on the Armada host; null when the attempt ran elsewhere, the runtime
        /// reports no version, or the probe did not answer in time.
        /// </summary>
        public string? RuntimeVersion { get; set; } = null;

        /// <summary>Operating-system process identifier of the attempt, used to match its exit.</summary>
        public int? ProcessId { get; set; } = null;

        /// <summary>UTC time the process launched.</summary>
        public DateTime StartedUtc { get; set; } = DateTime.UtcNow;

        /// <summary>UTC time the attempt ended; null while it runs.</summary>
        public DateTime? EndedUtc { get; set; } = null;

        /// <summary>How the attempt ended.</summary>
        public MissionAttemptOutcomeEnum Outcome { get; set; } = MissionAttemptOutcomeEnum.Running;

        /// <summary>Exit code of the process; null while it runs or when the runtime reported none.</summary>
        public int? ExitCode { get; set; } = null;

        /// <summary>Bounded machine reason code for the outcome.</summary>
        public string? ReasonCode { get; set; } = null;
    }
}
