namespace Armada.Core.Services
{
    /// <summary>
    /// Environment variable name used by runtime configuration for a caller-scoped MCP credential.
    /// Mission and chat launches set a scoped session token under the matching variable.
    /// </summary>
    public static class McpLaunchCredential
    {
        #region Public-Members

        /// <summary>
        /// Environment variable referenced by runtime MCP configuration.
        /// </summary>
        public const string EnvironmentVariable = "ARMADA_MCP_TOKEN";

        #endregion
    }
}
