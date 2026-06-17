namespace Armada.Core.Services
{
    using Armada.Core.Settings;

    /// <summary>
    /// Shared resolver for OpenCode connection parameters.
    /// Centralizes BaseUrl, Username, Password, and captain coding-agent resolution
    /// for the agent runtime CLI adapter.
    /// </summary>
    public sealed class OpenCodeConnection
    {
        #region Private-Members

        private readonly OpenCodeServerSettings _Settings;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate from explicit settings.
        /// </summary>
        /// <param name="settings">OpenCode server settings.</param>
        public OpenCodeConnection(OpenCodeServerSettings settings)
        {
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Resolve the base URL for the OpenCode server, falling back to the
        /// default localhost address when the configured value is blank.
        /// </summary>
        public string ResolveBaseUrl()
        {
            string baseUrl = _Settings.BaseUrl ?? string.Empty;
            if (string.IsNullOrWhiteSpace(baseUrl))
                baseUrl = "http://127.0.0.1:4096";
            return baseUrl.Trim().TrimEnd('/');
        }

        /// <summary>
        /// Resolve the username for Basic auth, falling back to "opencode" when blank.
        /// </summary>
        public string ResolveUsername()
        {
            string username = _Settings.Username ?? string.Empty;
            if (string.IsNullOrWhiteSpace(username))
                username = "opencode";
            return username;
        }

        /// <summary>
        /// Resolve the password for Basic auth. Returns an empty string when not
        /// configured; callers must omit the auth argument rather than send an empty
        /// password header.
        /// </summary>
        public string ResolvePassword()
        {
            return _Settings.Password ?? string.Empty;
        }

        /// <summary>
        /// Resolve the OpenCode coding/build agent name, falling back to "build" when blank.
        /// </summary>
        public string ResolveAgent()
        {
            string agent = _Settings.CaptainAgent ?? string.Empty;
            if (string.IsNullOrWhiteSpace(agent))
                agent = "build";
            return agent;
        }

        #endregion
    }
}
