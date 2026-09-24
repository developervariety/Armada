namespace Armada.Core.Services
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;

    /// <summary>
    /// Resolves the MCP credential a mission captain carries: the mission owner's own scoped session token. A
    /// mission launch and every probe that describes that launch resolve it here, so they present the same owner.
    /// </summary>
    public static class MissionMcpCredentialResolver
    {
        #region Public-Methods

        /// <summary>
        /// Build the MCP credential for a mission. The owner is the mission's tenant and user, and when the mission
        /// carries neither (older records, or a mission created without them), the objective owner carried on the
        /// mission's voyage. The endpoint scopes the token to that owner, exactly as an authenticated caller of that
        /// scope would receive, so the mission reaches only that tenant and user's records and no operator-only
        /// tool. When no owner resolves, or no session-token service is available, the credential carries no value
        /// (fail closed): nothing is presented and the endpoint refuses it, never the admiral launch credential.
        /// </summary>
        /// <param name="mission">The mission.</param>
        /// <param name="database">Database used to read the mission's voyage.</param>
        /// <param name="sessionTokens">Session token service, or null when none is available.</param>
        /// <param name="warn">Receives the reason when a credential carries no value; may be null.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The mission's scoped MCP credential; never null.</returns>
        public static async Task<McpCredentialReference> ResolveAsync(
            Mission mission,
            DatabaseDriver database,
            ISessionTokenService? sessionTokens,
            Action<string>? warn = null,
            CancellationToken token = default)
        {
            if (mission == null) throw new ArgumentNullException(nameof(mission));
            if (database == null) throw new ArgumentNullException(nameof(database));
            if (sessionTokens == null) return McpCredentialReference.MissionUnresolvedOwner;

            string? tenantId = mission.TenantId;
            string? userId = mission.UserId;

            // Autonomous dispatch has no interactive caller: the owner is the objective owner, which the
            // mission carries directly and, failing that, its voyage carries.
            if ((String.IsNullOrWhiteSpace(tenantId) || String.IsNullOrWhiteSpace(userId))
                && !String.IsNullOrWhiteSpace(mission.VoyageId))
            {
                Voyage? voyage = await database.Voyages.ReadAsync(mission.VoyageId!, token).ConfigureAwait(false);
                if (voyage != null)
                {
                    if (String.IsNullOrWhiteSpace(tenantId)) tenantId = voyage.TenantId;
                    if (String.IsNullOrWhiteSpace(userId)) userId = voyage.UserId;
                }
            }

            if (String.IsNullOrWhiteSpace(tenantId) || String.IsNullOrWhiteSpace(userId))
            {
                warn?.Invoke("mission " + mission.Id + " has no resolvable owner, so it carries no Armada MCP credential");
                return McpCredentialReference.MissionUnresolvedOwner;
            }

            AuthenticateResult issued = sessionTokens.CreateToken(tenantId!, userId!);
            if (String.IsNullOrWhiteSpace(issued.Token))
            {
                warn?.Invoke("no session token was issued for mission " + mission.Id + ", so it carries no Armada MCP credential");
                return McpCredentialReference.MissionUnresolvedOwner;
            }

            return McpCredentialReference.ForMission(issued.Token!);
        }

        #endregion
    }
}
