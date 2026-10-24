namespace Armada.Core.Enums
{
    using System.Runtime.Serialization;
    using System.Text.Json.Serialization;

    /// <summary>
    /// Outcome of a request to stop one job on a Harbor.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum HarborJobStopStatusEnum
    {
        /// <summary>
        /// The stop was sent to the connected Harbor; it stops the job and reports the exit.
        /// </summary>
        [EnumMember(Value = "Requested")]
        Requested,

        /// <summary>
        /// The job had already ended; nothing was sent.
        /// </summary>
        [EnumMember(Value = "AlreadyEnded")]
        AlreadyEnded,

        /// <summary>
        /// The Harbor is not connected, so the stop could not be delivered. A Harbor stops jobs the Admiral no longer
        /// owns when it reconnects.
        /// </summary>
        [EnumMember(Value = "HarborNotConnected")]
        HarborNotConnected,

        /// <summary>
        /// No such job on that Harbor, or the Harbor is not visible to the caller.
        /// </summary>
        [EnumMember(Value = "NotFound")]
        NotFound
    }
}
