namespace Armada.Core.Models
{
    using System.Collections.Generic;
    using System.Text.Json.Serialization;

    /// <summary>One mission in a versioned Architect plan.</summary>
    public sealed class VersionedArchitectPlanMission
    {
        /// <summary>Mission category.</summary>
        [JsonPropertyName("kind")]
        public string? Kind { get; set; }

        /// <summary>Acceptance evidence linked to requirements and decisions.</summary>
        [JsonPropertyName("acceptanceCriteria")]
        public List<VersionedArchitectAcceptanceCriterion> AcceptanceCriteria { get; set; } = new List<VersionedArchitectAcceptanceCriterion>();

        /// <summary>1-based decision indexes for this mission.</summary>
        [JsonPropertyName("decisionIndexes")]
        public List<int>? DecisionIndexes { get; set; }

        /// <summary>Exact validation commands.</summary>
        [JsonPropertyName("validationCommands")]
        public List<string> ValidationCommands { get; set; } = new List<string>();

        /// <summary>Mission title.</summary>
        [JsonPropertyName("title")]
        public string? Title { get; set; }

        /// <summary>Mission instructions.</summary>
        [JsonPropertyName("description")]
        public string? Description { get; set; }

        /// <summary>Optional 1-based index of an earlier mission.</summary>
        [JsonPropertyName("dependsOn")]
        public int? DependsOn { get; set; }

        /// <summary>Wait for parallel missions before starting this mission.</summary>
        [JsonPropertyName("waitForOtherMissions")]
        public bool WaitForOtherMissions { get; set; }
    }
}
