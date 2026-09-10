namespace Armada.Core.Services.Interfaces
{
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;

    /// <summary>
    /// Service for generic OAuth2 / OIDC single sign-on (Authorization-Code flow
    /// with PKCE). Exchanges provider codes for identity and mints an Armada
    /// session token.
    /// </summary>
    public interface IOAuth2Service
    {
        /// <summary>
        /// Whether OAuth2 single sign-on is enabled and fully configured.
        /// </summary>
        bool IsEnabled { get; }

        /// <summary>
        /// Public, secret-free configuration for the dashboard.
        /// </summary>
        /// <returns>OAuth config result.</returns>
        OAuthConfigResult GetPublicConfig();

        /// <summary>
        /// Begin an Authorization-Code login: generate state + PKCE, store the
        /// flow, and return the full provider authorization URL to redirect to.
        /// </summary>
        /// <param name="redirectUri">Callback URI the provider will redirect back to.</param>
        /// <returns>Provider authorization URL.</returns>
        string BuildAuthorizationUrl(string redirectUri);

        /// <summary>
        /// Complete the login: validate state, exchange the code, fetch userinfo,
        /// resolve or provision the user, and mint a session token.
        /// </summary>
        /// <param name="code">Authorization code from the provider.</param>
        /// <param name="state">Opaque state value from the provider redirect.</param>
        /// <param name="redirectUri">The same callback URI used to start the flow.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Login result.</returns>
        Task<OAuthLoginResult> CompleteLoginAsync(string? code, string? state, string redirectUri, CancellationToken token = default);

        /// <summary>
        /// Resolve an existing user by email in the configured tenant, or
        /// provision a new one when auto-provisioning is enabled.
        /// </summary>
        /// <param name="email">Email / identity from the provider.</param>
        /// <param name="displayName">Optional display name from the provider.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Resolved user, or null when the user cannot be signed in.</returns>
        Task<UserMaster?> ResolveOrProvisionUserAsync(string email, string? displayName, CancellationToken token = default);
    }
}
