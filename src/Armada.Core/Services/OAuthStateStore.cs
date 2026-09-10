namespace Armada.Core.Services
{
    using System;
    using System.Collections.Concurrent;
    using Armada.Core.Models;

    /// <summary>
    /// In-memory, single-use store for in-flight OAuth2 login flows. Correlates
    /// the opaque "state" value returned by the provider with the PKCE verifier.
    /// Entries are single-use, unguessable (256-bit), expire, and are purged on
    /// access, which defeats CSRF token forgery and replay.
    ///
    /// Known limitation: state is not bound to the initiating browser (Armada is
    /// token-in-localStorage with no pre-auth cookie), so a "login CSRF" where an
    /// attacker feeds their own valid code+state to a victim is not fully
    /// prevented. Closing that requires a short-lived SameSite/HttpOnly state
    /// cookie set at /authorize and verified at /callback -- tracked as a
    /// follow-up hardening.
    /// </summary>
    public class OAuthStateStore
    {
        #region Private-Members

        private readonly ConcurrentDictionary<string, OAuthFlowState> _States = new ConcurrentDictionary<string, OAuthFlowState>();
        private readonly int _TtlSeconds;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="ttlSeconds">Lifetime of a flow state in seconds (clamped to [30, 1800]).</param>
        public OAuthStateStore(int ttlSeconds = 300)
        {
            if (ttlSeconds < 30) ttlSeconds = 30;
            if (ttlSeconds > 1800) ttlSeconds = 1800;
            _TtlSeconds = ttlSeconds;
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Issue a new flow, returning the opaque state value to send to the provider.
        /// </summary>
        /// <param name="codeVerifier">PKCE code verifier.</param>
        /// <returns>Opaque state value.</returns>
        public string Issue(string codeVerifier)
        {
            PurgeExpired();
            string state = PkceHelper.GenerateOpaqueToken();
            _States[state] = new OAuthFlowState
            {
                CodeVerifier = codeVerifier ?? string.Empty,
                ExpiresUtc = DateTime.UtcNow.AddSeconds(_TtlSeconds)
            };
            return state;
        }

        /// <summary>
        /// Validate and consume a state value (single-use). Returns null if the
        /// state is unknown, already used, or expired.
        /// </summary>
        /// <param name="state">Opaque state value from the provider redirect.</param>
        /// <returns>Flow state, or null.</returns>
        public OAuthFlowState? Consume(string? state)
        {
            if (string.IsNullOrEmpty(state)) return null;
            if (!_States.TryRemove(state, out OAuthFlowState? flow)) return null;
            if (flow == null) return null;
            if (flow.ExpiresUtc <= DateTime.UtcNow) return null;
            return flow;
        }

        #endregion

        #region Private-Methods

        private void PurgeExpired()
        {
            DateTime now = DateTime.UtcNow;
            foreach (System.Collections.Generic.KeyValuePair<string, OAuthFlowState> kvp in _States)
            {
                if (kvp.Value.ExpiresUtc <= now)
                    _States.TryRemove(kvp.Key, out OAuthFlowState? _);
            }
        }

        #endregion
    }
}
