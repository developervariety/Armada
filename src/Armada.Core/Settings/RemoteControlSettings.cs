namespace Armada.Core.Settings
{
    /// <summary>
    /// Settings for Armada's outbound remote-control tunnel.
    /// </summary>
    public class RemoteControlSettings
    {
        #region Public-Members

        /// <summary>
        /// Whether the outbound remote-control tunnel is enabled.
        /// </summary>
        public bool Enabled { get; set; } = false;

        /// <summary>
        /// Full proxy tunnel endpoint URL.
        /// Accepts ws/wss directly, or http/https which are normalized to ws/wss.
        /// </summary>
        public string? TunnelUrl
        {
            get => _TunnelUrl;
            set => _TunnelUrl = String.IsNullOrWhiteSpace(value) ? Constants.DefaultRemoteTunnelUrl : value;
        }

        /// <summary>
        /// Optional instance identifier override.
        /// When null/empty, Armada derives an instance identifier automatically.
        /// </summary>
        public string? InstanceId { get; set; } = null;

        /// <summary>
        /// Optional enrollment token sent during tunnel handshake.
        /// </summary>
        public string? EnrollmentToken { get; set; } = null;

        /// <summary>
        /// Connection timeout in seconds.
        /// </summary>
        public int ConnectTimeoutSeconds
        {
            get => _ConnectTimeoutSeconds;
            set
            {
                if (value < 5 || value > 300) throw new ArgumentOutOfRangeException(nameof(ConnectTimeoutSeconds), "Must be in range [5, 300]");
                _ConnectTimeoutSeconds = value;
            }
        }

        /// <summary>
        /// Heartbeat interval in seconds while connected.
        /// </summary>
        public int HeartbeatIntervalSeconds
        {
            get => _HeartbeatIntervalSeconds;
            set
            {
                if (value < 5 || value > 300) throw new ArgumentOutOfRangeException(nameof(HeartbeatIntervalSeconds), "Must be in range [5, 300]");
                _HeartbeatIntervalSeconds = value;
            }
        }

        /// <summary>
        /// Base reconnect delay in seconds after a failed connection attempt.
        /// </summary>
        public int ReconnectBaseDelaySeconds
        {
            get => _ReconnectBaseDelaySeconds;
            set
            {
                if (value < 1 || value > 300) throw new ArgumentOutOfRangeException(nameof(ReconnectBaseDelaySeconds), "Must be in range [1, 300]");
                _ReconnectBaseDelaySeconds = value;
            }
        }

        /// <summary>
        /// Maximum reconnect delay in seconds.
        /// </summary>
        public int ReconnectMaxDelaySeconds
        {
            get => _ReconnectMaxDelaySeconds;
            set
            {
                if (value < 1 || value > 3600) throw new ArgumentOutOfRangeException(nameof(ReconnectMaxDelaySeconds), "Must be in range [1, 3600]");
                _ReconnectMaxDelaySeconds = value;
            }
        }

        /// <summary>
        /// Whether to allow invalid TLS certificates for the tunnel endpoint.
        /// Intended only for development environments.
        /// </summary>
        public bool AllowInvalidCertificates { get; set; } = false;

        #endregion

        #region Private-Members

        private int _ConnectTimeoutSeconds = Constants.DefaultRemoteConnectTimeoutSeconds;
        private int _HeartbeatIntervalSeconds = Constants.DefaultRemoteHeartbeatIntervalSeconds;
        private int _ReconnectBaseDelaySeconds = Constants.DefaultRemoteReconnectBaseDelaySeconds;
        private int _ReconnectMaxDelaySeconds = Constants.DefaultRemoteReconnectMaxDelaySeconds;
        private string? _TunnelUrl = Constants.DefaultRemoteTunnelUrl;

        #endregion
    }
}
