namespace Armada.Core.Models
{
    /// <summary>
    /// Request or event describing websocket closure.
    /// </summary>
    public class RemoteTunnelWebSocketCloseRequest
    {
        #region Public-Members

        /// <summary>
        /// Stable proxy-side browser websocket identifier.
        /// </summary>
        public string ProxySocketId { get; set; } = String.Empty;

        /// <summary>
        /// Optional websocket close status code.
        /// </summary>
        public int? Code { get; set; } = null;

        /// <summary>
        /// Optional websocket close reason.
        /// </summary>
        public string? Reason { get; set; } = null;

        #endregion
    }
}
