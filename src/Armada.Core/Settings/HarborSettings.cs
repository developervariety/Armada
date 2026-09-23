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

        /// <summary>
        /// Seconds one runner owner lookup may take. Every handshake, heartbeat, launch, stop and runner event
        /// re-reads the runner's durable enrollment, principal and credential; a lookup that does not finish in time
        /// is refused as <c>runner_owner_lookup_timeout</c>.
        /// </summary>
        public int OwnerLookupTimeoutSeconds
        {
            get => _OwnerLookupTimeoutSeconds;
            set
            {
                if (value < 1 || value > 60) throw new ArgumentOutOfRangeException(nameof(OwnerLookupTimeoutSeconds), "Must be in range [1, 60]");
                _OwnerLookupTimeoutSeconds = value;
            }
        }

        /// <summary>
        /// Routes that opt a captain or a vessel into running missions on a named runner. Empty by default, so every
        /// mission runs locally until a route is added, and routes have no effect while Harbor is disabled.
        /// </summary>
        public System.Collections.Generic.List<HarborMissionRoute> MissionRoutes
        {
            get => _MissionRoutes;
            set => _MissionRoutes = value ?? new System.Collections.Generic.List<HarborMissionRoute>();
        }

        /// <summary>
        /// Seconds a runner may stay disconnected before its live jobs are lost, like a local process that died.
        /// A runner that reconnects inside this window rebinds its jobs.
        /// </summary>
        public int DisconnectedJobGraceSeconds
        {
            get => _DisconnectedJobGraceSeconds;
            set
            {
                if (value < 10 || value > 86400) throw new ArgumentOutOfRangeException(nameof(DisconnectedJobGraceSeconds), "Must be in range [10, 86400]");
                _DisconnectedJobGraceSeconds = value;
            }
        }

        #endregion

        #region Private-Members

        private string _LinkPath = "/harbor/link";
        private int _HandshakeTimeoutSeconds = 15;
        private int _IdleTimeoutSeconds = 90;
        private int _OwnerLookupTimeoutSeconds = 5;
        private System.Collections.Generic.List<HarborMissionRoute> _MissionRoutes = new System.Collections.Generic.List<HarborMissionRoute>();
        private int _DisconnectedJobGraceSeconds = 180;

        #endregion
    }
}
