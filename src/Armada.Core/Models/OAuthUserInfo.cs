namespace Armada.Core.Models
{
    using System.Text.Json.Serialization;

    /// <summary>
    /// Strongly-typed OIDC userinfo response covering the standard claims
    /// Authentik and other generic OAuth2/OIDC providers return.
    /// </summary>
    public class OAuthUserInfo
    {
        #region Public-Members

        /// <summary>
        /// Subject identifier (stable, provider-unique user id).
        /// </summary>
        [JsonPropertyName("sub")]
        public string? Sub { get; set; } = null;

        /// <summary>
        /// Email address claim.
        /// </summary>
        [JsonPropertyName("email")]
        public string? Email { get; set; } = null;

        /// <summary>
        /// Whether the provider has verified ownership of the email (OIDC email_verified claim).
        /// </summary>
        [JsonPropertyName("email_verified")]
        public bool? EmailVerified { get; set; } = null;

        /// <summary>
        /// Preferred username claim.
        /// </summary>
        [JsonPropertyName("preferred_username")]
        public string? PreferredUsername { get; set; } = null;

        /// <summary>
        /// Full display name claim.
        /// </summary>
        [JsonPropertyName("name")]
        public string? Name { get; set; } = null;

        /// <summary>
        /// Given (first) name claim.
        /// </summary>
        [JsonPropertyName("given_name")]
        public string? GivenName { get; set; } = null;

        /// <summary>
        /// Family (last) name claim.
        /// </summary>
        [JsonPropertyName("family_name")]
        public string? FamilyName { get; set; } = null;

        #endregion

        #region Public-Methods

        /// <summary>
        /// Resolve a configured claim name to its value using the standard claims.
        /// </summary>
        /// <param name="claimName">Claim name from settings (e.g. "email", "preferred_username", "sub", "name").</param>
        /// <returns>Claim value, or null if not present / not a recognized claim.</returns>
        public string? GetClaim(string claimName)
        {
            if (string.IsNullOrWhiteSpace(claimName)) return null;

            switch (claimName.ToLowerInvariant())
            {
                case "sub": return Sub;
                case "email": return Email;
                case "preferred_username": return PreferredUsername;
                case "name": return Name;
                case "given_name": return GivenName;
                case "family_name": return FamilyName;
                default: return null;
            }
        }

        #endregion
    }
}
