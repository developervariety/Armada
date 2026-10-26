namespace Armada.Core.Models
{
    using System.Collections.Generic;
    using System.Text.Json.Serialization;

    /// <summary>Versioned JSON contract emitted in an armada-plan fence.</summary>
    public sealed class VersionedArchitectPlan
    {
        /// <summary>Supported contract versions are 2 and 3.</summary>
        [JsonPropertyName("contractVersion")]
        public int? ContractVersion { get; set; }

        /// <summary>Stable requirement IDs for the plan.</summary>
        [JsonPropertyName("requirements")]
        public List<string> Requirements { get; set; } = new List<string>();

        /// <summary>Resolved decisions for the plan.</summary>
        [JsonPropertyName("decisions")]
        public List<string> Decisions { get; set; } = new List<string>();

        /// <summary>Missions in dependency order.</summary>
        [JsonPropertyName("missions")]
        public List<VersionedArchitectPlanMission> Missions { get; set; } = new List<VersionedArchitectPlanMission>();
    }

}
