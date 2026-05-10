namespace Armada.Core.Memory
{
    using System.Collections.Generic;
    using System.Text.Json.Serialization;

    /// <summary>
    /// Parsed shape of a pack-curate reflections-candidate JSON block (v2-F1).
    /// Produced by the consolidator and consumed by the accept tool.
    /// </summary>
    public sealed class PackCurateCandidate
    {
        /// <summary>Hints to insert as new vessel_pack_hints rows.</summary>
        [JsonPropertyName("addHints")]
        public List<PackCurateAddHint>? AddHints { get; set; }

        /// <summary>Hints to update in place (id + changes).</summary>
        [JsonPropertyName("modifyHints")]
        public List<PackCurateModifyHint>? ModifyHints { get; set; }

        /// <summary>Hints to deactivate (id + reason).</summary>
        [JsonPropertyName("disableHints")]
        public List<PackCurateDisableHint>? DisableHints { get; set; }
    }

    /// <summary>One proposed new pack hint.</summary>
    public sealed class PackCurateAddHint
    {
        /// <summary>Regex applied to dispatch goal text. Validated case-insensitively.</summary>
        [JsonPropertyName("goalPattern")]
        public string? GoalPattern { get; set; }

        /// <summary>Glob paths the pack must include when this hint matches.</summary>
        [JsonPropertyName("mustInclude")]
        public List<string>? MustInclude { get; set; }

        /// <summary>Glob paths the pack must exclude when this hint matches.</summary>
        [JsonPropertyName("mustExclude")]
        public List<string>? MustExclude { get; set; }

        /// <summary>Application priority. Higher applied first.</summary>
        [JsonPropertyName("priority")]
        public int? Priority { get; set; }

        /// <summary>Confidence rating: high | medium | low.</summary>
        [JsonPropertyName("confidence")]
        public string? Confidence { get; set; }

        /// <summary>Free-text rationale.</summary>
        [JsonPropertyName("justification")]
        public string? Justification { get; set; }

        /// <summary>Mission ids that produced this hint.</summary>
        [JsonPropertyName("sourceMissionIds")]
        public List<string>? SourceMissionIds { get; set; }
    }

    /// <summary>One proposed modification to an existing pack hint row.</summary>
    public sealed class PackCurateModifyHint
    {
        /// <summary>Existing hint id (vph_ prefix).</summary>
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        /// <summary>Subset of fields to update.</summary>
        [JsonPropertyName("changes")]
        public PackCurateHintChanges? Changes { get; set; }
    }

    /// <summary>Field-level changes to apply to an existing pack hint.</summary>
    public sealed class PackCurateHintChanges
    {
        /// <summary>New goalPattern when set.</summary>
        [JsonPropertyName("goalPattern")]
        public string? GoalPattern { get; set; }

        /// <summary>New mustInclude when set.</summary>
        [JsonPropertyName("mustInclude")]
        public List<string>? MustInclude { get; set; }

        /// <summary>New mustExclude when set.</summary>
        [JsonPropertyName("mustExclude")]
        public List<string>? MustExclude { get; set; }

        /// <summary>New priority when set.</summary>
        [JsonPropertyName("priority")]
        public int? Priority { get; set; }

        /// <summary>New confidence when set.</summary>
        [JsonPropertyName("confidence")]
        public string? Confidence { get; set; }

        /// <summary>New justification when set.</summary>
        [JsonPropertyName("justification")]
        public string? Justification { get; set; }
    }

    /// <summary>One proposed deactivation of an existing pack hint.</summary>
    public sealed class PackCurateDisableHint
    {
        /// <summary>Existing hint id (vph_ prefix).</summary>
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        /// <summary>Free-text reason for retirement (recorded for audit).</summary>
        [JsonPropertyName("reason")]
        public string? Reason { get; set; }
    }
}
