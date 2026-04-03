namespace Armada.Core.Models
{
    /// <summary>
    /// Handshake payload sent by an Armada instance when the tunnel connects.
    /// </summary>
    public class RemoteTunnelHandshakePayload
    {
        #region Public-Members

        /// <summary>
        /// Tunnel protocol version.
        /// </summary>
        public string? ProtocolVersion { get; set; } = null;

        /// <summary>
        /// Armada release version.
        /// </summary>
        public string? ArmadaVersion { get; set; } = null;

        /// <summary>
        /// Stable instance identifier.
        /// </summary>
        public string? InstanceId { get; set; } = null;

        /// <summary>
        /// Optional enrollment token.
        /// </summary>
        public string? EnrollmentToken { get; set; } = null;

        /// <summary>
        /// Capability names supported by the instance.
        /// </summary>
        public List<string> Capabilities { get; set; } = new List<string>();

        #endregion
    }
}
