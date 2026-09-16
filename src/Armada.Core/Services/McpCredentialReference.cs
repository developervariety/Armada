namespace Armada.Core.Services
{
    using System;

    /// <summary>
    /// One MCP credential a launched captain presents, named by the environment variable that carries its
    /// value and referenced by that variable name in every runtime configuration file, so the value itself
    /// never lands in a dock or a scoped configuration file.
    ///
    /// Every launch presents a caller-scoped session token, never the admiral launch credential (which maps
    /// to global admin in the default tenant). A mission launch carries the mission owner's own session
    /// token (<see cref="ForMission"/>), and a chat turn carries the authenticated caller's own session
    /// token (<see cref="ForChat"/>); the MCP endpoint re-reads the token owner on every request and scopes
    /// the captain to that tenant and user, so a mission or chat captain never reaches a tool that owner may
    /// not, nor another tenant's records. A chat turn with no authenticated caller carries no token
    /// (<see cref="ChatUnauthenticated"/>): the variable is left unset, so the runtime presents no
    /// credential and the endpoint refuses it.
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
        /// A mission launch for a resolved mission owner: the launch variable carries the mission owner's
        /// own session token, which the MCP endpoint scopes to that owner's tenant and user. The admiral
        /// launch credential (global admin) is never presented, so a mission never gains global admin nor
        /// reaches another tenant's records. The variable name is the launch variable so every runtime's
        /// dock and scoped MCP configuration keeps referencing it; only the value carried is scoped.
        /// </summary>
        /// <param name="sessionToken">The mission owner's session token.</param>
        /// <returns>The credential reference.</returns>
        public static McpCredentialReference ForMission(string sessionToken)
        {
            if (String.IsNullOrWhiteSpace(sessionToken)) throw new ArgumentNullException(nameof(sessionToken));
            return new McpCredentialReference(McpLaunchCredential.EnvironmentVariable, sessionToken);
        }

        /// <summary>
        /// A mission launch whose owner could not be resolved: the launch variable is named but left unset,
        /// so the runtime presents no credential and reaches no MCP tool. Failing closed keeps the admiral
        /// launch credential out of the environment rather than falling back to global admin.
        /// </summary>
        public static McpCredentialReference MissionUnresolvedOwner { get; } =
            new McpCredentialReference(McpLaunchCredential.EnvironmentVariable, null);

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
