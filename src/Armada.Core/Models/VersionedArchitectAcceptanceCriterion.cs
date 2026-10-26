namespace Armada.Core.Models
{
    using System.Collections.Generic;
    using System.Text.Json.Serialization;

    /// <summary>One falsifiable acceptance criterion.</summary>
    public sealed class VersionedArchitectAcceptanceCriterion
    {
        /// <summary>Stable criterion ID.</summary>
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        /// <summary>Requirement IDs covered by this criterion.</summary>
        [JsonPropertyName("requirementIds")]
        public List<string> RequirementIds { get; set; } = new List<string>();

        /// <summary>1-based plan decision indexes checked here.</summary>
        [JsonPropertyName("decisionIndexes")]
        public List<int>? DecisionIndexes { get; set; }

        /// <summary>Scenario to verify.</summary>
        [JsonPropertyName("scenario")]
        public string? Scenario { get; set; }

        /// <summary>Expected observable result.</summary>
        [JsonPropertyName("expected")]
        public string? Expected { get; set; }

        /// <summary>Named verification.</summary>
        [JsonPropertyName("verification")]
        public string? Verification { get; set; }

        /// <summary>Failure and boundary cases. Null is invalid in a versioned contract.</summary>
        [JsonPropertyName("negativeCases")]
        public List<string>? NegativeCases { get; set; } = new List<string>();

        /// <summary>Reason no negative cases apply.</summary>
        [JsonPropertyName("negativeCasesNotApplicableReason")]
        public string? NegativeCasesNotApplicableReason { get; set; }
    }
}
