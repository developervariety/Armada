namespace Armada.Core.Enums
{
    using System.Runtime.Serialization;
    using System.Text.Json.Serialization;

    /// <summary>
    /// What kind of record a coordination claim reserves.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum CoordinationClaimSubjectEnum
    {
        /// <summary>
        /// A vessel (repository) - the claim covers dispatches against it.
        /// </summary>
        [EnumMember(Value = "Vessel")]
        Vessel,

        /// <summary>
        /// An objective or backlog item - the claim covers work on its scope.
        /// </summary>
        [EnumMember(Value = "Objective")]
        Objective
    }
}
