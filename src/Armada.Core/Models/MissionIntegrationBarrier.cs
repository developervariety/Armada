namespace Armada.Core.Models
{
    using System.Collections.Generic;
    using Armada.Core.Enums;

    /// <summary>Durable membership and scope for one final Architect integration mission.</summary>
    public sealed class MissionIntegrationBarrier
    {
        /// <summary>Stored document format version.</summary>
        public int Version { get; set; } = 1;

        /// <summary>Current lifecycle phase.</summary>
        public MissionIntegrationBarrierPhaseEnum Phase { get; set; } = MissionIntegrationBarrierPhaseEnum.Preparing;

        /// <summary>Architect mission that accepted the plan.</summary>
        public string ArchitectMissionId { get; set; } = string.Empty;

        /// <summary>Stable hash of the canonical accepted plan graph.</summary>
        public string PlanHash { get; set; } = string.Empty;

        /// <summary>Mission row that owns this barrier.</summary>
        public string IntegrationMissionId { get; set; } = string.Empty;

        /// <summary>Current tenant binding of the integration mission.</summary>
        public string? TenantId { get; set; }

        /// <summary>Current user binding of the integration mission.</summary>
        public string? UserId { get; set; }

        /// <summary>Current vessel binding of the integration mission.</summary>
        public string? VesselId { get; set; }

        /// <summary>Current voyage binding of the integration mission.</summary>
        public string? VoyageId { get; set; }

        /// <summary>Original voyage that accepted this plan; retained across trusted rescue cloning.</summary>
        public string OriginVoyageId { get; set; } = string.Empty;

        /// <summary>Number of plan branches required before acceptance.</summary>
        public int ExpectedBranchCount { get; set; }

        /// <summary>Exact plan branch terminals. Empty while preparing.</summary>
        public List<MissionIntegrationBarrierMember> Members { get; set; } = new List<MissionIntegrationBarrierMember>();
    }
}
