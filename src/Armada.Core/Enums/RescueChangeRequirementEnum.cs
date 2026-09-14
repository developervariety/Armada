namespace Armada.Core.Enums
{
    using System.Runtime.Serialization;
    using System.Text.Json.Serialization;

    /// <summary>
    /// What a rescue must change before its run counts as an attempt at the defect.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum RescueChangeRequirementEnum
    {
        /// <summary>
        /// Report-only work (Audit or Research): no change set is expected, and none is evidence either way.
        /// </summary>
        [EnumMember(Value = "None")]
        None,

        /// <summary>
        /// The deliverable is a committed document: any committed change, documentation included,
        /// is the work. Only an empty change set is ineffective.
        /// </summary>
        [EnumMember(Value = "AnyCommittedChange")]
        AnyCommittedChange,

        /// <summary>
        /// The deliverable is behavior: an empty or documentation-only change set is ineffective.
        /// </summary>
        [EnumMember(Value = "BehaviorChange")]
        BehaviorChange
    }
}
