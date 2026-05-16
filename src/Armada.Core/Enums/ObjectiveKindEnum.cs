namespace Armada.Core.Enums
{
    using System.Runtime.Serialization;
    using System.Text.Json.Serialization;

    /// <summary>
    /// Backlog/objective classification.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum ObjectiveKindEnum
    {
        /// <summary>
        /// Represents the feature objective kind.
        /// </summary>
        [EnumMember(Value = "Feature")]
        Feature,
        /// <summary>
        /// Represents the bug objective kind.
        /// </summary>
        [EnumMember(Value = "Bug")]
        Bug,
        /// <summary>
        /// Represents the refactor objective kind.
        /// </summary>
        [EnumMember(Value = "Refactor")]
        Refactor,
        /// <summary>
        /// Represents the research objective kind.
        /// </summary>
        [EnumMember(Value = "Research")]
        Research,
        /// <summary>
        /// Represents the chore objective kind.
        /// </summary>
        [EnumMember(Value = "Chore")]
        Chore,
        /// <summary>
        /// Represents the initiative objective kind.
        /// </summary>
        [EnumMember(Value = "Initiative")]
        Initiative
    }
}
