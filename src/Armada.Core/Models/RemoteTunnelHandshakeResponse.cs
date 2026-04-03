namespace Armada.Core.Models
{
    /// <summary>
    /// Control-plane response payload returned after handshake validation.
    /// </summary>
    public class RemoteTunnelHandshakeResponse
    {
        #region Public-Members

        /// <summary>
        /// Whether the handshake was accepted.
        /// </summary>
        public bool Accepted { get; set; } = false;

        /// <summary>
        /// Control-plane release version.
        /// </summary>
        public string? ControlPlaneVersion { get; set; } = null;

        /// <summary>
        /// Negotiated protocol version.
        /// </summary>
        public string? ProtocolVersion { get; set; } = null;

        /// <summary>
        /// Stable instance identifier registered by the control plane.
        /// </summary>
        public string? InstanceId { get; set; } = null;

        /// <summary>
        /// Optional informational message.
        /// </summary>
        public string? Message { get; set; } = null;

        /// <summary>
        /// Control-plane capability list advertised back to the instance.
        /// </summary>
        public List<string> Capabilities { get; set; } = new List<string>();

        #endregion
    }
}
