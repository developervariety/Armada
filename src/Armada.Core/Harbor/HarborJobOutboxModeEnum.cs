namespace Armada.Core.Harbor
{
    using System.Text.Json.Serialization;

    /// <summary>
    /// How a Harbor's job outbox treats the job messages it has sent on the current link.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum HarborJobOutboxModeEnum
    {
        /// <summary>
        /// The Admiral has not answered the handshake yet: sent messages are kept, in case the link turns out to be dead.
        /// </summary>
        Pending,

        /// <summary>
        /// The Admiral acknowledges job messages: sent messages are kept until it acknowledges them, and replayed on the
        /// next link when it does not.
        /// </summary>
        Acknowledged,

        /// <summary>
        /// The Admiral predates job acknowledgements: a message is dropped once it is written to the link, and only
        /// messages never written are replayed.
        /// </summary>
        Legacy
    }
}
