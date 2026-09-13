namespace Armada.Core.Enums
{
    using System.Text.Json.Serialization;

    /// <summary>
    /// Whether the latest recorded outcome of a mission read projection can be reported.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum RecordedHistoryStateEnum
    {
        /// <summary>
        /// The latest record was read and is reported.
        /// </summary>
        Recorded,

        /// <summary>
        /// No record exists in the caller's scope. This is not evidence of any outcome.
        /// </summary>
        NotRecorded,

        /// <summary>
        /// A latest record exists but cannot be reported (unreadable, unknown version, or ambiguous order).
        /// An older record is never reported in its place.
        /// </summary>
        Unavailable
    }
}
