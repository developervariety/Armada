namespace Armada.Core.Harbor
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services;

    /// <summary>
    /// Authenticated state for one Harbor link. The state binds every accepted message to the session lease the
    /// registry issued for the verified upgrade principal.
    /// </summary>
    public sealed class HarborRunnerConnection
    {
        #region Private-Members

        private const int MaximumLiveJobs = 1024;
        private readonly HarborRunnerSessionRegistry _Registry;
        private HarborRunnerSession? _Session;

        #endregion

        #region Constructors-and-Factories

        /// <summary>Instantiate a connection state bound to a session registry.</summary>
        /// <param name="registry">Registry that owns runner sessions.</param>
        public HarborRunnerConnection(HarborRunnerSessionRegistry registry)
        {
            _Registry = registry ?? throw new ArgumentNullException(nameof(registry));
        }

        #endregion

        #region Public-Members

        /// <summary>Current authenticated session, or null before a valid handshake.</summary>
        public HarborRunnerSession? Session { get { return _Session; } }

        /// <summary>Whether the Harbor handshake has been accepted.</summary>
        public bool IsHandshaken { get { return _Session != null; } }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Accept the first handshake. The runner identifier is matched to the verified principal by the durable
        /// owner resolver; no owner fields come from the handshake or from request headers.
        /// </summary>
        /// <param name="handshake">Typed handshake message.</param>
        /// <param name="authenticated">Principal verified by the authentication service at upgrade.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Accepted when the handshake creates a live session; otherwise the stable rejection reason.</returns>
        public async Task<HarborRunnerCheck> AcceptHandshakeAsync(HarborHandshake handshake, AuthContext authenticated, CancellationToken token = default)
        {
            string? invalid = ValidateHandshake(handshake, authenticated);
            if (invalid != null) return HarborRunnerCheck.Refuse(invalid);

            HarborRunnerRegistration registration = await _Registry.RegisterAsync(handshake.HarborId, authenticated, token).ConfigureAwait(false);
            if (!registration.Accepted || registration.Session == null)
                return HarborRunnerCheck.Refuse(String.IsNullOrWhiteSpace(registration.FailureReason) ? "harbor_registration_rejected" : registration.FailureReason);
            _Session = registration.Session;
            return HarborRunnerCheck.Pass;
        }

        /// <summary>
        /// Revalidate a heartbeat against the current durable owner and session generation.
        /// </summary>
        /// <param name="heartbeat">Typed heartbeat message.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Accepted when the heartbeat is authorized; otherwise the stable rejection reason.</returns>
        public async Task<HarborRunnerCheck> AcceptHeartbeatAsync(HarborHeartbeat heartbeat, CancellationToken token = default)
        {
            if (_Session == null) return HarborRunnerCheck.Refuse("harbor_handshake_required");
            if (heartbeat == null || heartbeat.LiveJobIds == null || heartbeat.LiveJobIds.Count > MaximumLiveJobs)
                return HarborRunnerCheck.Refuse("harbor_heartbeat_invalid");
            foreach (string jobId in heartbeat.LiveJobIds)
            {
                if (String.IsNullOrWhiteSpace(jobId) || jobId.Length > 200) return HarborRunnerCheck.Refuse("harbor_job_id_invalid");
            }
            return await _Registry.RevalidateAsync(_Session, token).ConfigureAwait(false);
        }

        /// <summary>Remove the connection's session from the registry when the link closes.</summary>
        public void Disconnect()
        {
            if (_Session != null) _Registry.TryDisconnect(_Session);
            _Session = null;
        }

        #endregion

        #region Private-Methods

        private string? ValidateHandshake(HarborHandshake handshake, AuthContext authenticated)
        {
            if (handshake == null)
            {
                return "harbor_handshake_missing";
            }
            if (_Session != null)
            {
                return "harbor_handshake_duplicate";
            }
            if (authenticated == null || !authenticated.IsAuthenticated)
            {
                return "harbor_identity_unverified";
            }
            if (!String.Equals(handshake.ProtocolVersion, HarborProtocol.Version, StringComparison.Ordinal))
            {
                return "harbor_protocol_version_unsupported";
            }
            if (String.IsNullOrWhiteSpace(handshake.HarborId) || handshake.HarborId.Length > 450)
            {
                return "harbor_id_invalid";
            }
            if (String.IsNullOrWhiteSpace(handshake.Name) || handshake.Name.Length > 200)
            {
                return "harbor_name_invalid";
            }
            if (handshake.MaxConcurrentJobs < 1 || handshake.MaxConcurrentJobs > MaximumLiveJobs)
            {
                return "harbor_capacity_invalid";
            }
            if (handshake.Capabilities == null || handshake.Capabilities.Count > MaximumLiveJobs)
            {
                return "harbor_capabilities_invalid";
            }
            foreach (HarborCapability capability in handshake.Capabilities)
            {
                if (capability == null || capability.Name.Length > 200)
                {
                    return "harbor_capability_invalid";
                }
            }

            return null;
        }

        #endregion
    }
}
