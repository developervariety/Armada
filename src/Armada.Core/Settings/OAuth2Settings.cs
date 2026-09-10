namespace Armada.Core.Settings
{
    using System;

    /// <summary>
    /// Settings for generic OAuth2 / OIDC single sign-on to the web dashboard
    /// (e.g. Authentik). When enabled, the dashboard offers a redirect-based
    /// Authorization-Code login that mints a normal Armada session token.
    /// </summary>
    public class OAuth2Settings
    {
        #region Public-Members

        /// <summary>
        /// Whether OAuth2 / OIDC single sign-on is enabled.
        /// </summary>
        public bool Enabled { get; set; } = false;

        /// <summary>
        /// Label shown on the dashboard sign-in button (e.g. "Authentik").
        /// </summary>
        public string DisplayName
        {
            get => _DisplayName;
            set => _DisplayName = string.IsNullOrWhiteSpace(value) ? "Single Sign-On" : value;
        }

        /// <summary>
        /// Provider authorization endpoint URL
        /// (Authentik: https://authentik.example.com/application/o/authorize/).
        /// </summary>
        public string? AuthorizationEndpoint { get; set; } = null;

        /// <summary>
        /// Provider token endpoint URL
        /// (Authentik: https://authentik.example.com/application/o/token/).
        /// </summary>
        public string? TokenEndpoint { get; set; } = null;

        /// <summary>
        /// Provider userinfo endpoint URL
        /// (Authentik: https://authentik.example.com/application/o/userinfo/).
        /// </summary>
        public string? UserInfoEndpoint { get; set; } = null;

        /// <summary>
        /// OAuth2 client identifier issued by the provider.
        /// </summary>
        public string? ClientId { get; set; } = null;

        /// <summary>
        /// OAuth2 client secret issued by the provider.
        /// </summary>
        public string? ClientSecret { get; set; } = null;

        /// <summary>
        /// Space-delimited scopes to request.
        /// </summary>
        public string Scopes
        {
            get => _Scopes;
            set => _Scopes = string.IsNullOrWhiteSpace(value) ? "openid profile email" : value;
        }

        /// <summary>
        /// Explicit redirect URI registered with the provider. When null or empty,
        /// the callback URI is derived from the incoming request host plus
        /// "/api/v1/auth/oauth/callback".
        /// </summary>
        public string? RedirectUri { get; set; } = null;

        /// <summary>
        /// Whether to use PKCE (Proof Key for Code Exchange). Recommended.
        /// </summary>
        public bool UsePkce { get; set; } = true;

        /// <summary>
        /// Userinfo claim used as the user's email / identity.
        /// </summary>
        public string EmailClaim
        {
            get => _EmailClaim;
            set => _EmailClaim = string.IsNullOrWhiteSpace(value) ? "email" : value;
        }

        /// <summary>
        /// Userinfo claim used as the user's display name.
        /// </summary>
        public string NameClaim
        {
            get => _NameClaim;
            set => _NameClaim = string.IsNullOrWhiteSpace(value) ? "name" : value;
        }

        /// <summary>
        /// Whether to require the provider's "email_verified" claim to be true
        /// before trusting the email for identity mapping. Prevents account
        /// takeover via an unverified, attacker-chosen email. Leave enabled
        /// unless the provider is known not to emit the claim.
        /// </summary>
        public bool RequireVerifiedEmail { get; set; } = true;

        /// <summary>
        /// Whether to auto-provision a new Armada user on first successful SSO
        /// login. When false, only users an admin has already created (matched
        /// by email in the default tenant) may sign in.
        /// </summary>
        public bool AllowAutoProvision { get; set; } = true;

        /// <summary>
        /// Tenant into which SSO users are mapped / provisioned.
        /// </summary>
        public string DefaultTenantId
        {
            get => _DefaultTenantId;
            set => _DefaultTenantId = string.IsNullOrWhiteSpace(value) ? Constants.DefaultTenantId : value;
        }

        #endregion

        #region Private-Members

        private string _DisplayName = "Single Sign-On";
        private string _Scopes = "openid profile email";
        private string _EmailClaim = "email";
        private string _NameClaim = "name";
        private string _DefaultTenantId = Constants.DefaultTenantId;

        #endregion

        #region Public-Methods

        /// <summary>
        /// Whether the settings are complete enough to run the OAuth2 flow.
        /// </summary>
        /// <returns>True if enabled and all required endpoints/credentials are present.</returns>
        public bool IsConfigured()
        {
            return Enabled
                && !string.IsNullOrWhiteSpace(AuthorizationEndpoint)
                && !string.IsNullOrWhiteSpace(TokenEndpoint)
                && !string.IsNullOrWhiteSpace(UserInfoEndpoint)
                && !string.IsNullOrWhiteSpace(ClientId)
                && !string.IsNullOrWhiteSpace(ClientSecret);
        }

        #endregion
    }
}
