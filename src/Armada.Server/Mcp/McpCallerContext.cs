namespace Armada.Server.Mcp
{
    using System;
    using System.Threading;
    using Armada.Core.Models;

    /// <summary>
    /// The authenticated identity behind the MCP tool call that is running now.
    ///
    /// The HTTP transport authenticates each request and sets the caller for that request's async
    /// flow. The stdio host sets its explicit local operator identity once. A tool reads the caller
    /// through <see cref="Require"/>, which fails when no identity was set: there is no default
    /// context to fall back to, so a missing or rejected credential can never become authority.
    /// </summary>
    public static class McpCallerContext
    {
        #region Private-Members

        private static readonly AsyncLocal<AuthContext?> _Current = new AsyncLocal<AuthContext?>();

        #endregion

        #region Public-Members

        /// <summary>
        /// The authenticated caller of the current tool call, or null when none was set.
        /// </summary>
        public static AuthContext? Current => _Current.Value;

        #endregion

        #region Public-Methods

        /// <summary>
        /// Return the authenticated caller of the current tool call.
        /// </summary>
        /// <returns>The caller.</returns>
        /// <exception cref="UnauthorizedAccessException">No authenticated caller is set.</exception>
        public static AuthContext Require()
        {
            AuthContext? caller = _Current.Value;
            if (caller == null || !caller.IsAuthenticated)
                throw new UnauthorizedAccessException("This MCP tool call carries no authenticated caller.");
            return caller;
        }

        /// <summary>
        /// Set the caller for the current async flow until the returned scope is disposed.
        /// </summary>
        /// <param name="caller">Authenticated caller.</param>
        /// <returns>A scope that restores the previous caller when disposed.</returns>
        public static IDisposable Begin(AuthContext caller)
        {
            if (caller == null) throw new ArgumentNullException(nameof(caller));
            if (!caller.IsAuthenticated) throw new ArgumentException("Only an authenticated caller can be set.", nameof(caller));
            AuthContext? previous = _Current.Value;
            _Current.Value = caller;
            return new Scope(previous);
        }

        #endregion

        #region Private-Types

        private sealed class Scope : IDisposable
        {
            private readonly AuthContext? _Previous;
            private bool _Disposed;

            public Scope(AuthContext? previous)
            {
                _Previous = previous;
            }

            public void Dispose()
            {
                if (_Disposed) return;
                _Disposed = true;
                _Current.Value = _Previous;
            }
        }

        #endregion
    }
}
