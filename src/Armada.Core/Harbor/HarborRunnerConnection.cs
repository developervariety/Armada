namespace Armada.Core.Harbor
{
    using System;
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
        /// <param name="failureReason">Stable rejection reason.</param>
        /// <returns>True when the handshake creates a live session.</returns>
        public bool TryAcceptHandshake(HarborHandshake handshake, AuthContext authenticated, out string failureReason)
        {
            failureReason = String.Empty;
            if (handshake == null)
            {
                failureReason = "harbor_handshake_missing";
                return false;
            }
            if (_Session != null)
            {
                failureReason = "harbor_handshake_duplicate";
                return false;
            }
            if (authenticated == null || !authenticated.IsAuthenticated)
            {
                failureReason = "harbor_identity_unverified";
                return false;
            }
            if (!String.Equals(handshake.ProtocolVersion, HarborProtocol.Version, StringComparison.Ordinal))
            {
                failureReason = "harbor_protocol_version_unsupported";
                return false;
            }
            if (String.IsNullOrWhiteSpace(handshake.HarborId) || handshake.HarborId.Length > 450)
            {
                failureReason = "harbor_id_invalid";
                return false;
            }
            if (String.IsNullOrWhiteSpace(handshake.Name) || handshake.Name.Length > 200)
            {
                failureReason = "harbor_name_invalid";
                return false;
            }
            if (handshake.MaxConcurrentJobs < 1 || handshake.MaxConcurrentJobs > MaximumLiveJobs)
            {
                failureReason = "harbor_capacity_invalid";
                return false;
            }
            if (handshake.Capabilities == null || handshake.Capabilities.Count > MaximumLiveJobs)
            {
                failureReason = "harbor_capabilities_invalid";
                return false;
            }
            foreach (HarborCapability capability in handshake.Capabilities)
            {
                if (capability == null || capability.Name.Length > 200)
                {
                    failureReason = "harbor_capability_invalid";
                    return false;
                }
            }

            if (!_Registry.TryRegister(handshake.HarborId, authenticated, out HarborRunnerSession? session, out failureReason)
                || session == null)
            {
                if (String.IsNullOrWhiteSpace(failureReason)) failureReason = "harbor_registration_rejected";
                return false;
            }
            _Session = session;
            return true;
        }

        /// <summary>
        /// Revalidate a heartbeat against the current durable owner and session generation.
        /// </summary>
        /// <param name="heartbeat">Typed heartbeat message.</param>
        /// <param name="failureReason">Stable rejection reason.</param>
        /// <returns>True when the heartbeat is authorized.</returns>
        public bool TryAcceptHeartbeat(HarborHeartbeat heartbeat, out string failureReason)
        {
            failureReason = String.Empty;
            if (_Session == null)
            {
                failureReason = "harbor_handshake_required";
                return false;
            }
            if (heartbeat == null || heartbeat.LiveJobIds == null || heartbeat.LiveJobIds.Count > MaximumLiveJobs)
            {
                failureReason = "harbor_heartbeat_invalid";
                return false;
            }
            foreach (string jobId in heartbeat.LiveJobIds)
            {
                if (String.IsNullOrWhiteSpace(jobId) || jobId.Length > 200)
                {
                    failureReason = "harbor_job_id_invalid";
                    return false;
                }
            }
            return _Registry.TryRevalidate(_Session, out failureReason);
        }

        /// <summary>Remove the connection's session from the registry when the link closes.</summary>
        public void Disconnect()
        {
            if (_Session != null) _Registry.TryDisconnect(_Session);
            _Session = null;
        }

        #endregion
    }
}
