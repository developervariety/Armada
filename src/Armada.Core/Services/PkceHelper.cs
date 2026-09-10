namespace Armada.Core.Services
{
    using System;
    using System.Security.Cryptography;
    using System.Text;

    /// <summary>
    /// Helper for RFC 7636 PKCE (Proof Key for Code Exchange) and opaque
    /// URL-safe token generation.
    /// </summary>
    public static class PkceHelper
    {
        #region Public-Methods

        /// <summary>
        /// Generate a high-entropy PKCE code verifier (base64url, 43 chars).
        /// </summary>
        /// <returns>Code verifier.</returns>
        public static string GenerateCodeVerifier()
        {
            byte[] bytes = new byte[32];
            RandomNumberGenerator.Fill(bytes);
            return Base64UrlEncode(bytes);
        }

        /// <summary>
        /// Compute the S256 PKCE code challenge for a verifier.
        /// </summary>
        /// <param name="codeVerifier">Code verifier.</param>
        /// <returns>base64url-encoded SHA-256 challenge.</returns>
        public static string ComputeCodeChallenge(string codeVerifier)
        {
            if (string.IsNullOrEmpty(codeVerifier)) throw new ArgumentNullException(nameof(codeVerifier));
            byte[] hash = SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier));
            return Base64UrlEncode(hash);
        }

        /// <summary>
        /// Generate an opaque URL-safe random token (e.g. an OAuth "state" value).
        /// </summary>
        /// <returns>Random token.</returns>
        public static string GenerateOpaqueToken()
        {
            byte[] bytes = new byte[32];
            RandomNumberGenerator.Fill(bytes);
            return Base64UrlEncode(bytes);
        }

        #endregion

        #region Private-Methods

        private static string Base64UrlEncode(byte[] bytes)
        {
            return Convert.ToBase64String(bytes)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }

        #endregion
    }
}
