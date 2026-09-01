namespace Armada.Core.Settings
{
    using Armada.Core.Enums;

    /// <summary>
    /// Settings for the restricted Grok Bot lead surface and shared lead-cycle controller.
    /// Secret values are supplied through the process environment and are not stored here.
    /// </summary>
    public class GrokLeadSettings
    {
        #region Public-Members

        /// <summary>
        /// Enables the restricted Grok Bot MCP listener. The listener also requires the
        /// environment variable named by <see cref="BearerTokenEnvironmentVariable"/>.
        /// </summary>
        public bool Enabled { get; set; } = false;

        /// <summary>
        /// Advertise only read operations and lead-cycle status. Use this for a
        /// connection proof before the Bot can start cycles or change coordination state.
        /// </summary>
        public bool ReadOnly { get; set; } = true;

        /// <summary>
        /// Enables the narrow, cycle-bound dispatch tool. This does not expose the
        /// normal destructive or administrative MCP tools.
        /// </summary>
        public bool ControlledDispatchEnabled { get; set; } = false;

        /// <summary>
        /// Maximum number of missions accepted by one controlled Grok dispatch.
        /// Clamped to 1 through 10.
        /// </summary>
        public int MaxControlledDispatchMissions
        {
            get => _MaxControlledDispatchMissions;
            set => _MaxControlledDispatchMissions = Math.Max(1, Math.Min(10, value));
        }

        /// <summary>
        /// Address used by the restricted listener. Keep this on loopback behind a TLS reverse proxy.
        /// </summary>
        public string Hostname
        {
            get => _Hostname;
            set
            {
                if (String.IsNullOrWhiteSpace(value)) throw new ArgumentNullException(nameof(Hostname));
                _Hostname = value.Trim();
            }
        }

        /// <summary>
        /// Port used by the restricted listener.
        /// </summary>
        public int Port
        {
            get => _Port;
            set
            {
                if (value < 1 || value > 65535) throw new ArgumentOutOfRangeException(nameof(Port));
                _Port = value;
            }
        }

        /// <summary>
        /// Stable Armada participant key assigned to authenticated Grok Bot requests.
        /// </summary>
        public string ParticipantKey
        {
            get => _ParticipantKey;
            set
            {
                if (String.IsNullOrWhiteSpace(value)) throw new ArgumentNullException(nameof(ParticipantKey));
                _ParticipantKey = value.Trim();
            }
        }

        /// <summary>
        /// Name of the environment variable that contains the restricted MCP bearer token.
        /// </summary>
        public string BearerTokenEnvironmentVariable
        {
            get => _BearerTokenEnvironmentVariable;
            set
            {
                if (String.IsNullOrWhiteSpace(value)) throw new ArgumentNullException(nameof(BearerTokenEnvironmentVariable));
                _BearerTokenEnvironmentVariable = value.Trim();
            }
        }

        /// <summary>
        /// Public HTTPS origin used for the optional MCP OAuth proof flow. Leave this
        /// empty to disable OAuth discovery and keep static bearer authentication only.
        /// This proof flow stores clients and grants in memory and is not a production
        /// identity provider.
        /// </summary>
        public string? OAuthPublicBaseUrl
        {
            get => _OAuthPublicBaseUrl;
            set => _OAuthPublicBaseUrl = String.IsNullOrWhiteSpace(value) ? null : value.Trim().TrimEnd('/');
        }

        /// <summary>
        /// Initial operating mode when no durable mode-change event exists.
        /// </summary>
        public LeadOperatingModeEnum DefaultMode { get; set; } = LeadOperatingModeEnum.LegacyPrimary;

        /// <summary>
        /// Lead-cycle lease duration in minutes. Clamped to 5 through 60 minutes.
        /// </summary>
        public int CycleLeaseMinutes
        {
            get => _CycleLeaseMinutes;
            set => _CycleLeaseMinutes = Math.Max(5, Math.Min(60, value));
        }

        /// <summary>
        /// Time without Grok lead activity before the legacy lead can request standby fallback.
        /// Clamped to 60 through 1440 minutes.
        /// </summary>
        public int StandbyFallbackAfterMinutes
        {
            get => _StandbyFallbackAfterMinutes;
            set => _StandbyFallbackAfterMinutes = Math.Max(60, Math.Min(1440, value));
        }

        /// <summary>
        /// Minutes within which a board heartbeat from any participant other than the lead
        /// itself, or one of its own helpers, counts as an operator being present. While an
        /// operator is present, armada_lead_cycle_begin refuses with an operator-present
        /// reason, so the unattended lead runs only when nobody is watching. 0 disables the
        /// gate. Clamped to 0 through 1440 minutes.
        /// </summary>
        public int OperatorPresenceMinutes
        {
            get => _OperatorPresenceMinutes;
            set => _OperatorPresenceMinutes = Math.Max(0, Math.Min(1440, value));
        }

        #endregion

        #region Private-Members

        private string _Hostname = "127.0.0.1";
        private int _Port = 7892;
        private int _MaxControlledDispatchMissions = 3;
        private string _ParticipantKey = "armada-lead";
        private string _BearerTokenEnvironmentVariable = "ARMADA_GROK_MCP_TOKEN";
        private string? _OAuthPublicBaseUrl = null;
        private int _CycleLeaseMinutes = 40;
        private int _StandbyFallbackAfterMinutes = 130;
        private int _OperatorPresenceMinutes = 30;

        #endregion
    }
}
