namespace Armada.Server
{
    using System;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Runtimes.Mcp;
    using SyslogLogging;

    /// <summary>
    /// Issues the Armada MCP access an API-endpoint captain uses for one authenticated caller. Ask chat and the
    /// captain tools report both call this, so the tools a report lists are the tools a chat turn is offered.
    /// </summary>
    public static class CallerMcpToolAccessFactory
    {
        #region Public-Methods

        /// <summary>
        /// Build caller-bound MCP access. Access is issued only for an authenticated caller with a tenant and a
        /// user: the token is that caller's own session token, and the MCP endpoint re-reads the user and tenant on
        /// every request and applies the shared tool access policy, so the holder can list and call only the tools
        /// the caller may use, inside the caller's scope. Any other caller gets no access.
        /// </summary>
        /// <param name="caller">Authenticated caller, or null.</param>
        /// <param name="sessionTokens">Session token service, or null when none is configured.</param>
        /// <param name="mcpPort">Armada MCP port; zero or less means MCP is not served.</param>
        /// <param name="logging">Optional logging module for a token that could not be issued.</param>
        /// <returns>Caller-bound access, or null when none may be issued.</returns>
        public static CallerMcpToolAccess? Create(AuthContext? caller, ISessionTokenService? sessionTokens, int mcpPort, LoggingModule? logging = null)
        {
            if (caller == null || !caller.IsAuthenticated) return null;
            if (String.IsNullOrWhiteSpace(caller.TenantId) || String.IsNullOrWhiteSpace(caller.UserId)) return null;
            if (sessionTokens == null || mcpPort <= 0) return null;

            AuthenticateResult issued = sessionTokens.CreateToken(caller.TenantId!, caller.UserId!);
            if (String.IsNullOrWhiteSpace(issued.Token))
            {
                logging?.Warn("[CallerMcpToolAccessFactory] no session token was issued for an API-endpoint caller; no Armada MCP tools are available");
                return null;
            }

            return new CallerMcpToolAccess(ArmadaMcpConfigBuilder.GetMcpUrl(mcpPort), issued.Token!);
        }

        #endregion
    }
}
