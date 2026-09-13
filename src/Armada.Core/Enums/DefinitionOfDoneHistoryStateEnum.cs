namespace Armada.Core.Enums
{
    using System.Text.Json.Serialization;

    /// <summary>
    /// Whether a mission's latest recorded definition-of-done evaluation can be reported.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum DefinitionOfDoneHistoryStateEnum
    {
        /// <summary>
        /// The latest evaluation record was read and is reported.
        /// </summary>
        Recorded,

        /// <summary>
        /// No evaluation record exists in the caller's scope. This is not evidence of a pass or a failure.
        /// </summary>
        NotRecorded,

        /// <summary>
        /// A latest record exists but cannot be reported (unreadable, unknown version, or ambiguous order).
        /// An older record is never reported in its place.
        /// </summary>
        Unavailable
    }
}
