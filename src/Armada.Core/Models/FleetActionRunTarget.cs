namespace Armada.Core.Models
{
    /// <summary>Immutable target identity and dispatch input captured for a run.</summary>
    public sealed class FleetActionRunTarget
    {
        /// <summary>Stable identity within the run.</summary>
        public string TargetId { get; set; } = Constants.IdGenerator.GenerateKSortable("fat_", 24);
        /// <summary>Selected vessel identifier.</summary>
        public string VesselId { get; set; } = string.Empty;
        /// <summary>Vessel tenant identifier at snapshot time.</summary>
        public string VesselTenantIdSnapshot { get; set; } = string.Empty;
        /// <summary>Vessel owner user identifier at snapshot time.</summary>
        public string VesselUserIdSnapshot { get; set; } = string.Empty;
        /// <summary>Vessel display name at snapshot time.</summary>
        public string VesselNameSnapshot { get; set; } = string.Empty;
        /// <summary>Fully rendered prompt for this target.</summary>
        public string RenderedPrompt { get; set; } = string.Empty;
        /// <summary>Resolved pipeline identifier, when configured.</summary>
        public string? ResolvedPipelineId { get; set; }
        /// <summary>Resolved pipeline content hash, when configured.</summary>
        public string? ResolvedPipelineHash { get; set; }
    }
}
