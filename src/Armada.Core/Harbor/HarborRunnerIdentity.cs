namespace Armada.Core.Harbor
{
    using System;
    using Armada.Core.Models;

    /// <summary>
    /// The verified principal bound to a Harbor runner registration.
    /// </summary>
    public sealed class HarborRunnerIdentity
    {
        /// <summary>Runner identifier.</summary>
        public string RunnerId { get; }

        /// <summary>Verified tenant identifier.</summary>
        public string TenantId { get; }

        /// <summary>Verified user identifier.</summary>
        public string UserId { get; }

        /// <summary>Authentication method that produced the verified context.</summary>
        public string AuthMethod { get; }

        /// <summary>Credential identifier when the authentication method supplies one.</summary>
        public string? CredentialId { get; }

        private HarborRunnerIdentity(
            string runnerId,
            string tenantId,
            string userId,
            string authMethod,
            string? credentialId)
        {
            RunnerId = runnerId;
            TenantId = tenantId;
            UserId = userId;
            AuthMethod = authMethod;
            CredentialId = credentialId;
        }

        /// <summary>
        /// Create an identity from an authentication result that has already been verified by the
        /// authentication service.
        /// </summary>
        /// <param name="runnerId">Runner identifier.</param>
        /// <param name="auth">Verified authentication context.</param>
        /// <returns>Runner identity.</returns>
        /// <exception cref="UnauthorizedAccessException">Thrown when the context is not a verified user.</exception>
        public static HarborRunnerIdentity FromVerifiedAuthContext(string runnerId, AuthContext auth)
        {
            if (String.IsNullOrWhiteSpace(runnerId)) throw new ArgumentException("Runner identifier is required.", nameof(runnerId));
            if (auth == null) throw new ArgumentNullException(nameof(auth));
            if (!auth.IsAuthenticated
                || String.IsNullOrWhiteSpace(auth.TenantId)
                || String.IsNullOrWhiteSpace(auth.UserId)
                || String.IsNullOrWhiteSpace(auth.AuthMethod))
                throw new UnauthorizedAccessException("A verified tenant and user identity is required.");

            return new HarborRunnerIdentity(
                runnerId.Trim(),
                auth.TenantId.Trim(),
                auth.UserId.Trim(),
                auth.AuthMethod.Trim(),
                String.IsNullOrWhiteSpace(auth.CredentialId) ? null : auth.CredentialId.Trim());
        }

        /// <summary>Compare this identity with a fresh verified authentication context.</summary>
        /// <param name="auth">Authentication context to compare.</param>
        /// <returns>True when the principal and credential identity match.</returns>
        public bool Matches(AuthContext auth)
        {
            if (auth == null || !auth.IsAuthenticated) return false;
            return String.Equals(TenantId, auth.TenantId, StringComparison.Ordinal)
                && String.Equals(UserId, auth.UserId, StringComparison.Ordinal)
                && String.Equals(AuthMethod, auth.AuthMethod, StringComparison.Ordinal)
                && String.Equals(CredentialId, String.IsNullOrWhiteSpace(auth.CredentialId) ? null : auth.CredentialId.Trim(), StringComparison.Ordinal);
        }
    }
}
