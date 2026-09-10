namespace Armada.Server.WebSocket
{
    /// <summary>
    /// A WebSocket subscription request with an optional resume position.
    /// </summary>
    internal sealed class WebSocketSubscribeRequest
    {
        /// <summary>
        /// Message route.
        /// </summary>
        public string Route { get; set; } = "";

        /// <summary>
        /// Process event stream to resume, or null for a new subscription.
        /// </summary>
        public string? StreamId { get; set; }

        /// <summary>
        /// Last event cursor received by the client, or null for a new subscription.
        /// </summary>
        public long? Cursor { get; set; }

        /// <summary>
        /// Optional voyage scope for the authoritative reconciliation snapshot.
        /// </summary>
        public string? VoyageId { get; set; }
    }
}
