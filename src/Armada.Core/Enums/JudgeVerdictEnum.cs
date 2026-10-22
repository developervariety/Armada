namespace Armada.Core.Enums
{
    using System.Text.Json.Serialization;

    /// <summary>
    /// A Judge mission's verdict, read only from the structured <c>[ARMADA:VERDICT]</c> protocol line.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum JudgeVerdictEnum
    {
        /// <summary>
        /// No structured verdict: none was emitted, or several conflicting verdicts were emitted.
        /// </summary>
        None,

        /// <summary>
        /// The Judge approves the work.
        /// </summary>
        Pass,

        /// <summary>
        /// The Judge rejects the work.
        /// </summary>
        Fail,

        /// <summary>
        /// The Judge requests follow-up changes.
        /// </summary>
        NeedsRevision,

        /// <summary>
        /// The Judge reports that an acceptance criterion cannot be satisfied as written (it contradicts another
        /// requirement or a dependency's actual behavior). The stage goes to a person to decide; nothing is waived.
        /// </summary>
        SpecConflict
    }
}
