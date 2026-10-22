namespace Armada.Core.Enums
{
    using System.Runtime.Serialization;
    using System.Text.Json.Serialization;

    /// <summary>
    /// Kind of work that already owns an objective, so no second dispatch may start it.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum ObjectiveActiveWorkKindEnum
    {
        /// <summary>
        /// A voyage linked to the objective is still open or in progress.
        /// </summary>
        [EnumMember(Value = "ActiveVoyage")]
        ActiveVoyage,

        /// <summary>
        /// A Challenger is still ruling on a failed Judge of the objective; autonomous recovery
        /// dispatches the rescue when it rules.
        /// </summary>
        [EnumMember(Value = "JudgeChallengePending")]
        JudgeChallengePending
    }
}
