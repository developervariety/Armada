namespace Armada.Core.Services
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Authorization;
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
        /// Build the MCP credential for a mission. The owner is the mission's tenant and user; a part the mission
        /// does not carry comes from its voyage (the objective owner of an autonomous mission). A record without a
        /// tenant or user belongs to the default tenant or default user (<see cref="OwnershipPolicy.TenantOfRecord"/>
        /// and <see cref="OwnershipPolicy.UserOfRecord"/>), so a mission on a tenant-owned vessel runs as that
        /// owner. The endpoint scopes the token to the owner exactly as it scopes that owner's own session: the
        /// mission reaches that tenant and user's records with that user's privileges, never more. The owner must be
        /// an active user of the mission's tenant; otherwise, or when no session-token service is available, the
        /// credential carries no value (fail closed): nothing is presented and the endpoint refuses it, never the
        /// admiral launch credential.
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

            string ownerTenantId = OwnershipPolicy.TenantOfRecord(tenantId);
            string ownerUserId = OwnershipPolicy.UserOfRecord(userId);

            // A token names a tenant and a user, and the endpoint grants the user's own privileges. A user of
            // another tenant would carry its privileges into this tenant, so the owner must belong to it.
            UserMaster? owner = await database.Users.ReadByIdAsync(ownerUserId, token).ConfigureAwait(false);
            if (owner == null || !owner.Active || !String.Equals(owner.TenantId, ownerTenantId, StringComparison.Ordinal))
            {
                warn?.Invoke("mission " + mission.Id + " owner " + ownerUserId + " is not an active user of tenant " + ownerTenantId
                    + ", so the mission carries no Armada MCP credential");
                return McpCredentialReference.MissionUnresolvedOwner;
            }

            AuthenticateResult issued = sessionTokens.CreateToken(ownerTenantId, ownerUserId);
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
