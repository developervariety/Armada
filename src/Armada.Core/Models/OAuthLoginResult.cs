namespace Armada.Core.Models
{
    using System;

    /// <summary>
    /// Result of completing an OAuth2 login (code exchange + user resolution +
    /// session token minting).
    /// </summary>
    public class OAuthLoginResult
    {
        #region Public-Members

        /// <summary>
        /// Whether the login succeeded.
        /// </summary>
        public bool Success { get; set; } = false;

        /// <summary>
        /// Minted Armada session token (X-Token) when successful.
        /// </summary>
        public string? Token { get; set; } = null;

        /// <summary>
        /// Session token expiry when successful.
        /// </summary>
        public DateTime? ExpiresUtc { get; set; } = null;

        /// <summary>
        /// Short, safe error reason when unsuccessful (surfaced to the dashboard).
        /// </summary>
        public string? ErrorMessage { get; set; } = null;

        /// <summary>
        /// Create a failed result.
        /// </summary>
        /// <param name="error">Error reason.</param>
        /// <returns>Failed result.</returns>
        public static OAuthLoginResult Failed(string error)
        {
            return new OAuthLoginResult { Success = false, ErrorMessage = error };
        }

        #endregion
    }
}
