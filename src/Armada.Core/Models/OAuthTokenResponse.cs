namespace Armada.Core.Models
{
    using System.Text.Json.Serialization;

    /// <summary>
    /// Strongly-typed OAuth2 token endpoint response.
    /// </summary>
    public class OAuthTokenResponse
    {
        #region Public-Members

        /// <summary>
        /// Access token used to call the userinfo endpoint.
        /// </summary>
        [JsonPropertyName("access_token")]
        public string? AccessToken { get; set; } = null;

        /// <summary>
        /// Token type (typically "Bearer").
        /// </summary>
        [JsonPropertyName("token_type")]
        public string? TokenType { get; set; } = null;

        /// <summary>
        /// OIDC ID token, when the "openid" scope is requested.
        /// </summary>
        [JsonPropertyName("id_token")]
        public string? IdToken { get; set; } = null;

        /// <summary>
        /// Lifetime of the access token in seconds.
        /// </summary>
        [JsonPropertyName("expires_in")]
        public int? ExpiresIn { get; set; } = null;

        /// <summary>
        /// Granted scopes.
        /// </summary>
        [JsonPropertyName("scope")]
        public string? Scope { get; set; } = null;

        /// <summary>
        /// Error code returned by the provider when the exchange fails.
        /// </summary>
        [JsonPropertyName("error")]
        public string? Error { get; set; } = null;

        /// <summary>
        /// Human-readable error description returned by the provider.
        /// </summary>
        [JsonPropertyName("error_description")]
        public string? ErrorDescription { get; set; } = null;

        #endregion
    }
}
