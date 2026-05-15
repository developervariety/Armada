namespace Armada.Core.Settings
{
    /// <summary>
    /// Settings for local OpenCode daemon backed inference.
    /// </summary>
    public sealed class OpenCodeServerSettings
    {
        #region Public-Members

        /// <summary>
        /// Whether Armada should auto-launch the local OpenCode daemon when needed.
        /// </summary>
        public bool AutoLaunch { get; set; } = true;

        /// <summary>
        /// Base URL for OpenCode server API calls.
        /// </summary>
        public string BaseUrl
        {
            get => _BaseUrl;
            set => _BaseUrl = value ?? "http://127.0.0.1:4096";
        }

        /// <summary>
        /// Hostname passed to <c>opencode serve --hostname</c>.
        /// </summary>
        public string Hostname
        {
            get => _Hostname;
            set => _Hostname = value ?? "127.0.0.1";
        }

        /// <summary>
        /// Port passed to <c>opencode serve --port</c>.
        /// </summary>
        public int Port
        {
            get => _Port;
            set
            {
                if (value < 1024) value = 1024;
                if (value > 65535) value = 65535;
                _Port = value;
            }
        }

        /// <summary>
        /// Optional explicit executable path.
        /// </summary>
        public string ExecutablePath
        {
            get => _ExecutablePath;
            set => _ExecutablePath = value ?? string.Empty;
        }

        /// <summary>
        /// Provider id included in OpenCode model payloads.
        /// </summary>
        public string ProviderId
        {
            get => _ProviderId;
            set => _ProviderId = value ?? "opencode";
        }

        /// <summary>
        /// Model id included in OpenCode model payloads.
        /// </summary>
        public string ModelId
        {
            get => _ModelId;
            set => _ModelId = value ?? "deepseek-v4-flash-free";
        }

        /// <summary>
        /// Agent name used by session and message payloads.
        /// </summary>
        public string Agent
        {
            get => _Agent;
            set => _Agent = value ?? "summary";
        }

        /// <summary>
        /// Username for Basic auth to local server when password is configured.
        /// </summary>
        public string Username
        {
            get => _Username;
            set => _Username = value ?? "opencode";
        }

        /// <summary>
        /// Password for local OpenCode server auth.
        /// </summary>
        public string Password
        {
            get => _Password;
            set => _Password = value ?? string.Empty;
        }

        /// <summary>
        /// Startup timeout for health polling.
        /// </summary>
        public int StartupTimeoutSeconds
        {
            get => _StartupTimeoutSeconds;
            set
            {
                if (value < 5) value = 5;
                if (value > 300) value = 300;
                _StartupTimeoutSeconds = value;
            }
        }

        /// <summary>
        /// Request timeout for OpenCode session and message calls.
        /// </summary>
        public int RequestTimeoutSeconds
        {
            get => _RequestTimeoutSeconds;
            set
            {
                if (value < 5) value = 5;
                if (value > 600) value = 600;
                _RequestTimeoutSeconds = value;
            }
        }

        #endregion

        #region Private-Members

        private string _BaseUrl = "http://127.0.0.1:4096";
        private string _Hostname = "127.0.0.1";
        private int _Port = 4096;
        private string _ExecutablePath = string.Empty;
        private string _ProviderId = "opencode";
        private string _ModelId = "deepseek-v4-flash-free";
        private string _Agent = "summary";
        private string _Username = "opencode";
        private string _Password = string.Empty;
        private int _StartupTimeoutSeconds = 30;
        private int _RequestTimeoutSeconds = 60;

        #endregion
    }
}
