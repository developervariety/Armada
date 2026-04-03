namespace Armada.ControlPlane.Settings
{
    using Armada.Core;

    /// <summary>
    /// Runtime settings for the Armada remote control plane.
    /// </summary>
    public class ControlPlaneSettings
    {
        #region Public-Members

        /// <summary>
        /// Interface hostname to bind.
        /// </summary>
        public string Hostname { get; set; } = "localhost";

        /// <summary>
        /// HTTP port to bind.
        /// </summary>
        public int Port
        {
            get => _Port;
            set
            {
                if (value < 1 || value > 65535) throw new ArgumentOutOfRangeException(nameof(Port), "Must be in range [1, 65535]");
                _Port = value;
            }
        }

        /// <summary>
        /// Whether enrollment tokens are required for handshake acceptance.
        /// </summary>
        public bool RequireEnrollmentToken { get; set; } = false;

        /// <summary>
        /// Accepted enrollment tokens.
        /// </summary>
        public List<string> EnrollmentTokens { get; set; } = new List<string>();

        /// <summary>
        /// Initial handshake timeout in seconds.
        /// </summary>
        public int HandshakeTimeoutSeconds
        {
            get => _HandshakeTimeoutSeconds;
            set
            {
                if (value < 1 || value > 300) throw new ArgumentOutOfRangeException(nameof(HandshakeTimeoutSeconds), "Must be in range [1, 300]");
                _HandshakeTimeoutSeconds = value;
            }
        }

        /// <summary>
        /// Time after which a connected instance is considered stale without tunnel activity.
        /// </summary>
        public int StaleAfterSeconds
        {
            get => _StaleAfterSeconds;
            set
            {
                if (value < 5 || value > 86400) throw new ArgumentOutOfRangeException(nameof(StaleAfterSeconds), "Must be in range [5, 86400]");
                _StaleAfterSeconds = value;
            }
        }

        /// <summary>
        /// Timeout for live request/response calls over the tunnel.
        /// </summary>
        public int RequestTimeoutSeconds
        {
            get => _RequestTimeoutSeconds;
            set
            {
                if (value < 1 || value > 300) throw new ArgumentOutOfRangeException(nameof(RequestTimeoutSeconds), "Must be in range [1, 300]");
                _RequestTimeoutSeconds = value;
            }
        }

        /// <summary>
        /// Maximum retained recent events per instance.
        /// </summary>
        public int MaxRecentEvents
        {
            get => _MaxRecentEvents;
            set
            {
                if (value < 1 || value > 500) throw new ArgumentOutOfRangeException(nameof(MaxRecentEvents), "Must be in range [1, 500]");
                _MaxRecentEvents = value;
            }
        }

        /// <summary>
        /// Normalize configured enrollment tokens by trimming blanks.
        /// </summary>
        /// <returns>Distinct non-empty tokens.</returns>
        public HashSet<string> GetEnrollmentTokenSet()
        {
            return new HashSet<string>(
                EnrollmentTokens
                    .Where(token => !String.IsNullOrWhiteSpace(token))
                    .Select(token => token.Trim()),
                StringComparer.Ordinal);
        }

        #endregion

        #region Private-Members

        private int _Port = Constants.DefaultControlPlanePort;
        private int _HandshakeTimeoutSeconds = Constants.DefaultControlPlaneHandshakeTimeoutSeconds;
        private int _StaleAfterSeconds = Constants.DefaultControlPlaneStaleAfterSeconds;
        private int _RequestTimeoutSeconds = Constants.DefaultControlPlaneRequestTimeoutSeconds;
        private int _MaxRecentEvents = 50;

        #endregion
    }
}
