namespace Armada.Core.Database
{
    using System;

    /// <summary>Expected mission attempt and process identity for an atomic Harbor launch binding.</summary>
    public sealed class HarborMissionBindingUpdate
    {
        /// <summary>Mission identifier.</summary>
        public string MissionId { get; set; } = String.Empty;

        /// <summary>Assigned captain identifier.</summary>
        public string CaptainId { get; set; } = String.Empty;

        /// <summary>Assigned dock identifier.</summary>
        public string DockId { get; set; } = String.Empty;

        /// <summary>New durable Harbor job identifier.</summary>
        public string JobId { get; set; } = String.Empty;

        /// <summary>Previous job identifier, or null for the first attempt.</summary>
        public string? ExpectedJobId { get; set; }

        /// <summary>Exact mission owner tenant.</summary>
        public string? TenantId { get; set; }

        /// <summary>Exact mission owner user.</summary>
        public string? UserId { get; set; }

        /// <summary>Synthetic process identifier reserved by the Admiral.</summary>
        public int AdmiralProcessId { get; set; }

        /// <summary>UTC launch identity of the synthetic process.</summary>
        public DateTime ProcessStartedUtc { get; set; }

    }
}
