namespace Armada.Core.Models
{
    using System;
    using Armada.Core.Enums;

    /// <summary>An owner-scoped reusable mission action definition.</summary>
    public sealed class FleetAction
    {
        /// <summary>Action identifier.</summary>
        public string Id { get; set; } = Constants.IdGenerator.GenerateKSortable("fac_", 24);
        /// <summary>Owner tenant identifier.</summary>
        public string TenantId { get; set; } = string.Empty;
        /// <summary>Owner user identifier.</summary>
        public string UserId { get; set; } = string.Empty;
        /// <summary>Display name.</summary>
        public string Name { get; set; } = string.Empty;
        /// <summary>Whether this definition accepts new runs.</summary>
        public bool Active { get; set; }
        /// <summary>Action kind. Only mission actions are supported.</summary>
        public FleetActionKindEnum Kind { get; set; } = FleetActionKindEnum.Mission;
        /// <summary>Prompt template used to render each target.</summary>
        public string PromptTemplate { get; set; } = string.Empty;
        /// <summary>Optional pipeline identifier.</summary>
        public string? PipelineId { get; set; }
        /// <summary>Maximum concurrent targets, from one through eight.</summary>
        public int Concurrency { get; set; } = 1;
        /// <summary>Creation time in UTC.</summary>
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
        /// <summary>Last update time in UTC.</summary>
        public DateTime LastUpdateUtc { get; set; } = DateTime.UtcNow;
    }
}
