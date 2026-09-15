namespace Armada.Core.Services
{
    using System;

    /// <summary>
    /// One MCP credential a launched captain presents, named by the environment variable that carries its
    /// value and referenced by that variable name in every runtime configuration file, so the value itself
    /// never lands in a dock or a scoped configuration file.
    ///
    /// Two credentials exist. A mission launch carries this admiral's launch credential
    /// (<see cref="Launch"/>), which the MCP endpoint maps to operator access. A chat turn carries the
    /// authenticated caller's own session token (<see cref="ForChat"/>), which the endpoint re-reads on
    /// every request and scopes to that caller, so a chat captain never reaches a tool the caller may not.
    /// A chat turn with no authenticated caller carries no token (<see cref="ChatUnauthenticated"/>): the
    /// variable is left unset, so the runtime presents no credential and the endpoint refuses it.
    /// </summary>
    public sealed class McpCredentialReference
    {
        #region Public-Members

        /// <summary>
        /// Environment variable that carries the caller's session token into a launched chat captain.
        /// A distinct name from the launch credential variable, so a chat launch can never present the
        /// admiral launch credential.
        /// </summary>
        public const string ChatEnvironmentVariable = "ARMADA_MCP_CHAT_TOKEN";

        /// <summary>
        /// Name of the environment variable that carries the token value.
        /// </summary>
        public string EnvironmentVariable { get; }

        /// <summary>
        /// The token value, or empty when no credential is presented (the variable is then left unset).
        /// </summary>
        public string Token { get; }

        /// <summary>
        /// True when a token value is present and the launch must set the environment variable.
        /// </summary>
        public bool HasToken => !String.IsNullOrEmpty(Token);

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="environmentVariable">Environment variable name that carries the token.</param>
        /// <param name="token">Token value, or null/empty for no credential.</param>
        public McpCredentialReference(string environmentVariable, string? token)
        {
            if (String.IsNullOrWhiteSpace(environmentVariable)) throw new ArgumentNullException(nameof(environmentVariable));
            EnvironmentVariable = environmentVariable.Trim();
            Token = token == null ? String.Empty : token.Trim();
        }

        /// <summary>
        /// The admiral launch credential a mission launch presents.
        /// </summary>
        public static McpCredentialReference Launch { get; } =
            new McpCredentialReference(McpLaunchCredential.EnvironmentVariable, McpLaunchCredential.Token);

        /// <summary>
        /// A chat turn with no authenticated caller: the chat variable is named but left unset, so the
        /// runtime presents no credential and reaches no MCP tool.
        /// </summary>
        public static McpCredentialReference ChatUnauthenticated { get; } =
            new McpCredentialReference(ChatEnvironmentVariable, null);

        /// <summary>
        /// A chat turn for an authenticated caller: the chat variable carries the caller's own session token.
        /// </summary>
        /// <param name="sessionToken">The caller's session token.</param>
        /// <returns>The credential reference.</returns>
        public static McpCredentialReference ForChat(string sessionToken)
        {
            if (String.IsNullOrWhiteSpace(sessionToken)) throw new ArgumentNullException(nameof(sessionToken));
            return new McpCredentialReference(ChatEnvironmentVariable, sessionToken);
        }

        #endregion
    }
}
