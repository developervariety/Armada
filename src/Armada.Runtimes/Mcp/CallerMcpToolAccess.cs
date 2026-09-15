namespace Armada.Runtimes.Mcp
{
    using System;

    /// <summary>
    /// Armada MCP tool access for one API-endpoint run, bound to one authenticated caller. The session token is the
    /// caller's own credential, so the server lists and runs only the tools that caller may use, inside that
    /// caller's scope. It lives only in memory for the run and is never written to a log, a prompt or a file.
    /// </summary>
    public sealed class CallerMcpToolAccess
    {
        #region Public-Members

        /// <summary>
        /// Absolute Armada MCP endpoint URL.
        /// </summary>
        public string Endpoint { get; }

        /// <summary>
        /// The caller's session token.
        /// </summary>
        public string SessionToken { get; }

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="endpoint">Absolute Armada MCP endpoint URL.</param>
        /// <param name="sessionToken">The caller's session token.</param>
        public CallerMcpToolAccess(string endpoint, string sessionToken)
        {
            if (String.IsNullOrWhiteSpace(endpoint)) throw new ArgumentNullException(nameof(endpoint));
            if (String.IsNullOrWhiteSpace(sessionToken)) throw new ArgumentNullException(nameof(sessionToken));
            Endpoint = endpoint.Trim();
            SessionToken = sessionToken.Trim();
        }

        #endregion

        #region Public-Methods

        /// <inheritdoc />
        public override string ToString()
        {
            return "CallerMcpToolAccess(" + Endpoint + ")";
        }

        #endregion
    }
}
