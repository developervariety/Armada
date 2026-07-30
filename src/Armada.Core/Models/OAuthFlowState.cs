namespace Armada.Core.Models
{
    using System;

    /// <summary>
    /// Short-lived server-side state for an in-flight OAuth2 Authorization-Code
    /// login. Correlates the provider redirect with the original request and
    /// carries the PKCE code verifier. Single-use.
    /// </summary>
    public class OAuthFlowState
    {
        #region Public-Members

        /// <summary>
        /// PKCE code verifier generated when the flow started.
        /// </summary>
        public string CodeVerifier { get; set; } = string.Empty;

        /// <summary>
        /// UTC time after which this flow state is no longer valid.
        /// </summary>
        public DateTime ExpiresUtc { get; set; } = DateTime.UtcNow;

        #endregion
    }
}
