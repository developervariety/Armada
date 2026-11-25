namespace Armada.Core.Models
{
    using System;
    /// <summary>Immutable consumed-permit source binding for the same mission epoch.</summary>
    public sealed class AdministrativeRestartReceipt
    {
        /// <summary>Consumed permit.</summary>
        public string PermitId { get; set; } = String.Empty;
        /// <summary>Original mission.</summary>
        public string MissionId { get; set; } = String.Empty;
        /// <summary>Original voyage.</summary>
        public string VoyageId { get; set; } = String.Empty;
        /// <summary>Same original creation time; an ordinary later restart invalidates this epoch binding.</summary>
        public DateTime OriginalCreatedUtc { get; set; }
        /// <summary>Exact source to provision without inheriting the planner's unchanged branch.</summary>
        public string SourceCommit { get; set; } = String.Empty;
        /// <summary>Preserved dependency identity.</summary>
        public string? DependencyId { get; set; }
        /// <summary>Commit of the unchanged immediate dependency that the source must include.</summary>
        public string DependencyCommit { get; set; } = String.Empty;
        /// <summary>Complete prior corrected state; it preserves output, reason, counters and source.</summary>
        public string PriorSnapshot { get; set; } = String.Empty;
    }
}
