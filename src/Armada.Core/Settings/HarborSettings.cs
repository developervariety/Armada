namespace Armada.Core.Settings
{
    using System;

    /// <summary>
    /// Settings for the detached Harbor runner link. Harbor is disabled by default; when disabled the Admiral
    /// registers no Harbor WebSocket route and no Harbor enrollment routes.
    /// </summary>
    public class HarborSettings
    {
        #region Public-Members

        /// <summary>
        /// Whether the Admiral accepts Harbor runner links and exposes Harbor enrollment administration.
        /// </summary>
        public bool Enabled { get; set; } = false;

        /// <summary>
        /// WebSocket path the Harbor dials on the Admiral's REST port.
        /// </summary>
        public string LinkPath
        {
            get => _LinkPath;
            set
            {
                if (String.IsNullOrWhiteSpace(value) || !value.Trim().StartsWith("/", StringComparison.Ordinal))
                    throw new ArgumentException("LinkPath must be an absolute path.", nameof(LinkPath));
                _LinkPath = value.Trim();
            }
        }

        /// <summary>
        /// Seconds an authenticated link may stay open before its handshake arrives.
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
        /// Seconds without any frame after which the Admiral closes a handshaken link. Harbors send heartbeats
        /// well inside this window.
        /// </summary>
        public int IdleTimeoutSeconds
        {
            get => _IdleTimeoutSeconds;
            set
            {
                if (value < 5 || value > 3600) throw new ArgumentOutOfRangeException(nameof(IdleTimeoutSeconds), "Must be in range [5, 3600]");
                _IdleTimeoutSeconds = value;
            }
        }

        #endregion

        #region Private-Members

        private string _LinkPath = "/harbor/link";
        private int _HandshakeTimeoutSeconds = 15;
        private int _IdleTimeoutSeconds = 90;

        #endregion
    }
}
