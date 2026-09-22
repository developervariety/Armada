namespace Armada.Core.Services
{
    using Armada.Core.Settings;

    /// <summary>
    /// Shared resolver for OpenCode connection parameters: the captain coding-agent name the
    /// agent runtime CLI adapter passes as <c>--agent</c>.
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
