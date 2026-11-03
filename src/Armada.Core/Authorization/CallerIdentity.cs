namespace Armada.Core.Authorization
{
    using System;
    using Armada.Core.Models;

    /// <summary>
    /// The one name an operator action records for its caller: the principal display name, else the credential id,
    /// else the user id, each trimmed. It is a label for records and audit, never an authorization credential.
    /// </summary>
    public static class CallerIdentity
    {
        #region Public-Methods

        /// <summary>
        /// Name the caller.
        /// </summary>
        /// <param name="caller">Caller, or null.</param>
        /// <returns>The name, or null when there is no authenticated caller or it carries none of the three.</returns>
        public static string? Of(AuthContext? caller)
        {
            if (caller == null || !caller.IsAuthenticated) return null;
            if (!String.IsNullOrWhiteSpace(caller.PrincipalDisplay)) return caller.PrincipalDisplay!.Trim();
            if (!String.IsNullOrWhiteSpace(caller.CredentialId)) return caller.CredentialId!.Trim();
            return String.IsNullOrWhiteSpace(caller.UserId) ? null : caller.UserId!.Trim();
        }

        #endregion
    }
}
