namespace Armada.Server.Mcp
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading.Tasks;
    using Armada.Core.Authorization;
    using Armada.Core.Database;
    using Armada.Core.Models;

    /// <summary>
    /// Limits captain evidence reads to the calling mission's report chain and authorizing objectives.
    /// A mission token never inherits its owner's operator privileges.
    /// </summary>
    internal static class McpMissionEvidenceScope
    {
        /// <summary>Read a report source within the caller's evidence scope.</summary>
        public static async Task<Mission?> ReadMissionAsync(DatabaseDriver database, AuthContext caller, string id)
        {
            if (String.IsNullOrEmpty(caller.MissionId))
                return await CallerScopedRead.ReadMissionAsync(database, caller, id).ConfigureAwait(false);

            Mission? anchor = await ReadAnchorAsync(database, caller).ConfigureAwait(false);
            if (anchor == null) return null;
            // ID reads allow legacy null ownership; SameOwner is mandatory before any record leaves this scope.
            Mission? target = await database.Missions.ReadAsync(id).ConfigureAwait(false);
            if (target == null || !SameOwner(caller, target.TenantId, target.UserId)) return null;
            if (target.Id == anchor.Id
                || (!String.IsNullOrEmpty(anchor.VoyageId) && target.VoyageId == anchor.VoyageId)
                || target.Id == anchor.DependsOnMissionId
                || target.Id == anchor.ParentMissionId)
                return target;
            return null;
        }

        /// <summary>Read an authorizing objective or its directly linked parent without previewing its evidence.</summary>
        public static async Task<Objective?> ReadObjectiveAsync(DatabaseDriver database, AuthContext caller, string id)
        {
            Mission? anchor = await ReadAnchorAsync(database, caller).ConfigureAwait(false);
            if (anchor == null) return null;
            Objective? target = await database.Objectives.ReadAsync(id).ConfigureAwait(false);
            if (target == null || !SameOwner(caller, target.TenantId, target.UserId)) return null;
            if (Authorizes(target, anchor)) return target;

            List<Objective> objectives = OwnershipPolicy.TenantOf(caller) == Armada.Core.Constants.DefaultTenantId
                ? await database.Objectives.EnumerateAsync().ConfigureAwait(false)
                : await database.Objectives.EnumerateAsync(OwnershipPolicy.TenantOf(caller)).ConfigureAwait(false);
            if (objectives.Any(objective => SameOwner(caller, objective.TenantId, objective.UserId)
                && objective.ParentObjectiveId == target.Id
                && Authorizes(objective, anchor)))
                return target;
            return null;
        }

        private static bool Authorizes(Objective objective, Mission mission)
        {
            return objective.MissionIds.Contains(mission.Id, StringComparer.Ordinal)
                || (!String.IsNullOrEmpty(mission.VoyageId)
                    && objective.VoyageIds.Contains(mission.VoyageId, StringComparer.Ordinal));
        }

        private static bool SameOwner(AuthContext caller, string? tenantId, string? userId)
        {
            return OwnershipPolicy.SameTenant(tenantId, OwnershipPolicy.TenantOf(caller))
                && String.Equals(OwnershipPolicy.UserOfRecord(userId), OwnershipPolicy.UserOf(caller), StringComparison.Ordinal);
        }

        private static async Task<Mission?> ReadAnchorAsync(DatabaseDriver database, AuthContext caller)
        {
            if (String.IsNullOrEmpty(caller.MissionId)) return null;
            Mission? anchor = await database.Missions.ReadAsync(caller.MissionId).ConfigureAwait(false);
            return anchor != null && SameOwner(caller, anchor.TenantId, anchor.UserId) ? anchor : null;
        }
    }
}
