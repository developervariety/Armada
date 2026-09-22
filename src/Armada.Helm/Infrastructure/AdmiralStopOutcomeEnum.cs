namespace Armada.Helm.Infrastructure
{
    using System.Text.Json.Serialization;

    /// <summary>
    /// How a request to stop the Admiral ended.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum AdmiralStopOutcomeEnum
    {
        /// <summary>
        /// Nothing answered at the Admiral address before the stop request, so no request was sent.
        /// </summary>
        NotRunning,

        /// <summary>
        /// The Admiral accepted the stop request and then stopped answering.
        /// </summary>
        Stopped,

        /// <summary>
        /// The Admiral answered the stop request with a non-success status and keeps running.
        /// </summary>
        Refused,

        /// <summary>
        /// The Admiral was asked to stop but still answered when the wait ended.
        /// </summary>
        StillRunning
    }
}
