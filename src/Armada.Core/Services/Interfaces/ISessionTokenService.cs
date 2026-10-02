namespace Armada.Core.Services.Interfaces
{
    using Armada.Core.Models;

    /// <summary>
    /// Service for creating and validating encrypted session tokens.
    /// </summary>
    public interface ISessionTokenService
    {
        /// <summary>
        /// Create an encrypted session token for a tenant and user.
        /// </summary>
        /// <param name="tenantId">Tenant identifier.</param>
        /// <param name="userId">User identifier.</param>
        /// <returns>Authentication result with token and expiry.</returns>
        AuthenticateResult CreateToken(string tenantId, string userId);

        /// <summary>
        /// Validate and decrypt a session token.
        /// </summary>
        /// <param name="encryptedToken">Base64-encoded encrypted token.</param>
        /// <returns>AuthContext if valid, null if invalid or expired.</returns>
        AuthContext? ValidateToken(string encryptedToken);

        /// <summary>
        /// Create a session token for a captain's mission connection. It authenticates as the user and
        /// also names the mission, which <see cref="ValidateToken"/> returns as <see cref="AuthContext.MissionId"/>.
        /// The default issues a plain user token; an implementation that cannot carry the mission leaves
        /// mission-scoped policies unable to identify the caller.
        /// </summary>
        /// <param name="tenantId">Tenant ID.</param>
        /// <param name="userId">User ID.</param>
        /// <param name="missionId">Mission the connection belongs to.</param>
        /// <returns>The issued token.</returns>
        AuthenticateResult CreateMissionToken(string tenantId, string userId, string missionId)
        {
            return CreateToken(tenantId, userId);
        }
    }
}
