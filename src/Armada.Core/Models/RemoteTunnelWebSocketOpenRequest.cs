namespace Armada.Core.Models
{
    /// <summary>
    /// Request to open a local Armada dashboard websocket session on behalf of a proxy browser client.
    /// </summary>
    public class RemoteTunnelWebSocketOpenRequest
    {
        #region Public-Members

        /// <summary>
        /// Stable proxy-side browser websocket identifier.
        /// </summary>
        public string ProxySocketId { get; set; } = String.Empty;

        /// <summary>
        /// Requested websocket path.
        /// </summary>
        public string Path { get; set; } = "/ws";

        #endregion
    }
}
