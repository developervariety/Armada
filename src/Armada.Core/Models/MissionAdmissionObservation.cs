namespace Armada.Core.Models
{
    using System;
    using System.Text.Json.Serialization;

    /// <summary>A historical admission evaluation, not the mission's current blocking reason.</summary>
    public sealed class MissionAdmissionObservation
    {
        /// <summary>Evidence format version.</summary>
        [JsonRequired]
        public int Version { get; set; } = 1;

        /// <summary>Unique identity of this evaluation.</summary>
        [JsonRequired]
        public string ObservationId { get; set; } = Guid.NewGuid().ToString("N");

        /// <summary>Mission evaluated.</summary>
        public string MissionId { get; set; } = String.Empty;

        /// <summary>Tenant at evaluation.</summary>
        public string? TenantId { get; set; }

        /// <summary>Owner at evaluation.</summary>
        public string? UserId { get; set; }

        /// <summary>Vessel at evaluation.</summary>
        public string? VesselId { get; set; }

        /// <summary>UTC time of this observation.</summary>
        [JsonRequired]
        public DateTime ObservedUtc { get; set; } = DateTime.UtcNow;

        /// <summary>Whether the actual admission controls permitted launch.</summary>
        public bool Admit { get; set; }

        /// <summary>Bounded, redacted explanation from the controls.</summary>
        public string Reason { get; set; } = String.Empty;

        /// <summary>Global assigned and in-progress count used by the controls.</summary>
        public int GlobalActiveWorkloads { get; set; }

        /// <summary>Configured global limit; zero means unlimited.</summary>
        public int GlobalWorkloadLimit { get; set; }

        /// <summary>True when the global limit refused launch before probing pressure.</summary>
        public bool GlobalLimitReached { get; set; }

        /// <summary>The actual pressure decision; null when the global limit prevented evaluation.</summary>
        public ResourcePressureDecision? PressureDecision { get; set; }
    }
}
