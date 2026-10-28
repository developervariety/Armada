namespace Armada.Proxy.Models
{
    /// <summary>
    /// Body of POST /proxy-api/v1/auth/login.
    /// </summary>
    public class ProxyLoginRequest
    {
        #region Public-Members

        /// <summary>
        /// Challenge nonce issued by GET /proxy-api/v1/auth/challenge.
        /// </summary>
        public string? Nonce { get; set; } = null;

        /// <summary>
        /// Proof derived from the shared password and the nonce.
        /// </summary>
        public string? ProofSha256 { get; set; } = null;

        /// <summary>
        /// Whether the response sets the <c>armada_proxy_session</c> cookie. Null or true (browsers): it does. False
        /// (native clients, which send the returned token in the session header): no cookie is set, so the session is
        /// held only where the client keeps the token.
        /// </summary>
        public bool? SetCookie { get; set; } = null;

        #endregion
    }
}
