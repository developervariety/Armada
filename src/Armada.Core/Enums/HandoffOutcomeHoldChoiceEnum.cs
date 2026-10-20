namespace Armada.Core.Enums
{
    using System.Runtime.Serialization;
    using System.Text.Json.Serialization;

    /// <summary>The operator's choice for a stage handoff held by the <c>handoff_outcome</c> hold.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum HandoffOutcomeHoldChoiceEnum
    {
        /// <summary>Hand the stage off unchanged, with no unmet-criteria note.</summary>
        [EnumMember(Value = "Release")]
        Release,

        /// <summary>Prepend the unmet-criteria note to the next stage's brief, then hand the stage off.</summary>
        [EnumMember(Value = "MailAndRelease")]
        MailAndRelease,

        /// <summary>Fail the held stage and cancel its dependent stages, so autonomous recovery decides a rescue.</summary>
        [EnumMember(Value = "Rescue")]
        Rescue
    }
}
