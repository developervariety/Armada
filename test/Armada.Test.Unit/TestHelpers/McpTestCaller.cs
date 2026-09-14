namespace Armada.Test.Unit.TestHelpers
{
    using System;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Server.Mcp;

    /// <summary>
    /// Explicit caller identity for tests that call MCP tool handlers or dispatch services directly.
    /// Production handlers read the authenticated caller the transport sets and have no default, so a
    /// test that bypasses the transport must name the caller it acts as.
    /// </summary>
    public static class McpTestCaller
    {
        #region Public-Members

        /// <summary>
        /// A default-tenant global administrator, the identity an operator's MCP session authenticates as.
        /// </summary>
        public static AuthContext Operator => AuthContext.Authenticated(
            Armada.Core.Constants.DefaultTenantId,
            Armada.Core.Constants.DefaultUserId,
            true,
            true,
            "Test",
            null,
            "Test operator");

        #endregion

        #region Public-Methods

        /// <summary>
        /// Wrap a captured tool handler so every invocation runs as <see cref="Operator"/>.
        /// </summary>
        /// <param name="handler">Captured handler.</param>
        /// <returns>The wrapped handler.</returns>
        public static Func<JsonElement?, Task<object>> Wrap(Func<JsonElement?, Task<object>> handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            return async args =>
            {
                using (McpCallerContext.Begin(Operator))
                {
                    return await handler(args).ConfigureAwait(false);
                }
            };
        }

        #endregion
    }
}
