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

        private static readonly AsyncLocal<CancellationToken> _CallToken = new AsyncLocal<CancellationToken>();

        #endregion

        #region Public-Members

        /// <summary>
        /// The authenticated caller of the current tool call, or null when none was set.
        /// </summary>
        public static AuthContext? Current => _Current.Value;

        /// <summary>
        /// A label for the authenticated caller of the current tool call, for attribution only: the
        /// principal display name, else the credential id, else the user id. Null when no caller is set
        /// or the caller carries none of the three. It is not an authorization credential.
        /// </summary>
        public static string? CallerIdentity
        {
            get
            {
                AuthContext? caller = _Current.Value;
                if (caller == null || !caller.IsAuthenticated) return null;
                if (!String.IsNullOrWhiteSpace(caller.PrincipalDisplay)) return caller.PrincipalDisplay!.Trim();
                if (!String.IsNullOrWhiteSpace(caller.CredentialId)) return caller.CredentialId!.Trim();
                return String.IsNullOrWhiteSpace(caller.UserId) ? null : caller.UserId!.Trim();
            }
        }

        /// <summary>
        /// Cancellation of the tool call that is running now: the transport cancels it when the client cancels or
        /// disconnects. <see cref="CancellationToken.None"/> when the transport set none.
        /// </summary>
        public static CancellationToken CallToken => _CallToken.Value;

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
        /// Return the mission a tool call acts for. A captain credential names the mission it was issued
        /// to, and that mission wins over any mission id the call supplies, so a captain cannot attribute
        /// its call to another mission. Without such a credential the supplied id is used.
        /// </summary>
        /// <param name="supplied">The mission id the call supplied, or null.</param>
        /// <param name="ignoredSupplied">The supplied id when it named a different mission and was ignored; otherwise null.</param>
        /// <returns>The mission id the call acts for, or null when there is none.</returns>
        public static string? ResolveMissionId(string? supplied, out string? ignoredSupplied)
        {
            ignoredSupplied = null;
            string? own = _Current.Value?.MissionId;
            if (String.IsNullOrWhiteSpace(own)) return String.IsNullOrWhiteSpace(supplied) ? null : supplied!.Trim();
            if (!String.IsNullOrWhiteSpace(supplied) && !String.Equals(supplied!.Trim(), own, StringComparison.Ordinal))
                ignoredSupplied = supplied!.Trim();
            return own;
        }

        /// <summary>
        /// Return the supplied mission id, or the caller's own mission when none was supplied. Unlike
        /// <see cref="ResolveMissionId"/>, a supplied id is kept, because a read tool may name a mission
        /// the caller is allowed to read; only an omitted id defaults.
        /// </summary>
        /// <param name="supplied">The mission id the call supplied, or null.</param>
        /// <returns>The mission id to read, or null when the call supplied none and the caller has no mission.</returns>
        public static string? MissionIdOrOwn(string? supplied)
        {
            if (!String.IsNullOrWhiteSpace(supplied)) return supplied!.Trim();
            string? own = _Current.Value?.MissionId;
            return String.IsNullOrWhiteSpace(own) ? null : own;
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

        /// <summary>
        /// Set the cancellation of the current tool call for the current async flow until the returned scope is disposed.
        /// </summary>
        /// <param name="token">The transport's cancellation for this call.</param>
        /// <returns>A scope that restores the previous token when disposed.</returns>
        public static IDisposable BeginCall(CancellationToken token)
        {
            CancellationToken previous = _CallToken.Value;
            _CallToken.Value = token;
            return new CallScope(previous);
        }

        #endregion

        #region Private-Types

        private sealed class CallScope : IDisposable
        {
            private readonly CancellationToken _Previous;
            private bool _Disposed;

            public CallScope(CancellationToken previous)
            {
                _Previous = previous;
            }

            public void Dispose()
            {
                if (_Disposed) return;
                _Disposed = true;
                _CallToken.Value = _Previous;
            }
        }

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
