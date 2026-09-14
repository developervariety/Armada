namespace Armada.Core.Enums
{
    using System.Text.Json.Serialization;

    /// <summary>
    /// How an unsuppressed Slop finding affects the Slop check.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum SlopSeverityEnum
    {
        /// <summary>
        /// Unambiguous slop. An unsuppressed finding fails the check.
        /// </summary>
        Fail = 0,

        /// <summary>
        /// A pattern that a faithful source reproduction can legitimately contain. The finding is
        /// reported in the check output and the check still passes.
        /// </summary>
        Warn = 1
    }
}
