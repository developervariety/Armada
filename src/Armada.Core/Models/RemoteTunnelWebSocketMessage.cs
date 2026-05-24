namespace Armada.Core.Models
{
    /// <summary>
    /// Websocket message routed between Armada.Proxy and an Armada instance.
    /// </summary>
    public class RemoteTunnelWebSocketMessage
    {
        #region Public-Members

        /// <summary>
        /// Stable proxy-side browser websocket identifier.
        /// </summary>
        public string ProxySocketId { get; set; } = String.Empty;

        /// <summary>
        /// Text payload.
        /// </summary>
        public string Data { get; set; } = String.Empty;

        #endregion
    }
}
