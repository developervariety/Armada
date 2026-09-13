namespace Armada.Core.Models
{
    using System;
    using Armada.Core.Enums;

    /// <summary>
    /// Admission decision returned by the resource-pressure admission policy.
    /// </summary>
    public sealed class ResourcePressureDecision
    {
        /// <summary>
        /// Whether the mission/captain may be launched now.
        /// </summary>
        public bool Admit { get; set; }

        /// <summary>
        /// Human-readable reason explaining a deferral, or empty when admitted.
        /// </summary>
        public string Reason { get; set; } = string.Empty;

        /// <summary>Stable reason code supplied by the policy; Unknown for older implementations.</summary>
        public ResourcePressureReasonEnum ReasonCode { get; set; } = ResourcePressureReasonEnum.Unknown;

        /// <summary>UTC time of policy evaluation, or null when not supplied.</summary>
        public DateTime? EvaluatedUtc { get; set; }

        /// <summary>Whether normal pressure checks were enabled at evaluation.</summary>
        public bool? PressureEnabled { get; set; }

        /// <summary>Configured minimum available memory in bytes at evaluation.</summary>
        public long? MinimumAvailableMemoryBytes { get; set; }

        /// <summary>Configured build limit at evaluation; zero means unlimited.</summary>
        public int? MaximumConcurrentBuilds { get; set; }

        /// <summary>Workload count supplied to the policy.</summary>
        public int? ActiveBuildPressure { get; set; }

        /// <summary>Active OOM cooldown deadline at evaluation, if any.</summary>
        public DateTime? OomCooldownUntilUtc { get; set; }

        /// <summary>
        /// Resource-pressure snapshot captured during evaluation.
        /// </summary>
        public ResourcePressureSnapshot? Snapshot { get; set; }
    }
}