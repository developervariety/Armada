namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Authorization;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>Authorized identity, mission and vessel shared by recall entry points.</summary>
    public sealed class ContextRecallScope
    {
        /// <summary>Effective record visibility. Mission credentials never retain administrator privileges.</summary>
        public AuthContext Caller { get; private set; } = null!;

        /// <summary>Authorized active mission, when the call has mission scope.</summary>
        public Mission? Mission { get; private set; }

        /// <summary>Authorized vessel, when one is selected.</summary>
        public Vessel? Vessel { get; private set; }

        private ContextRecallScope() { }

        /// <summary>Resolve a scope before source reads or budget writes. A credential wins over supplied identifiers.</summary>
        /// <param name="database">Configured database.</param>
        /// <param name="caller">Authenticated transport identity.</param>
        /// <param name="vesselId">Operator vessel selection.</param>
        /// <param name="missionId">Compatibility operator mission selection.</param>
        /// <param name="requireVessel">Require a vessel for native and code retrieval.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Resolved caller scope.</returns>
        public static async Task<ContextRecallScope> ResolveAsync(DatabaseDriver database, AuthContext caller,
            string? vesselId = null, string? missionId = null, bool requireVessel = true, CancellationToken token = default)
        {
            if (caller == null || !caller.IsAuthenticated) throw new UnauthorizedAccessException("Authentication is required.");
            token.ThrowIfCancellationRequested();
            AuthContext effective = caller;
            if (!String.IsNullOrWhiteSpace(caller.MissionId))
            {
                effective = AuthContext.Authenticated(OwnershipPolicy.TenantOf(caller), OwnershipPolicy.UserOf(caller), false, false, caller.AuthMethod ?? "Mission");
                effective.MissionId = caller.MissionId;
                missionId = caller.MissionId;
            }
            ContextRecallScope scope = new ContextRecallScope { Caller = effective };
            if (!String.IsNullOrWhiteSpace(missionId))
            {
                scope.Mission = await ReadMissionAsync(database, effective, missionId!, token).ConfigureAwait(false);
                if (scope.Mission == null) throw new KeyNotFoundException("Mission not found in the caller's scope.");
                if (MissionStateMachine.IsTerminal(scope.Mission.Status)) throw new UnauthorizedAccessException("An active owned mission is required.");
                vesselId = scope.Mission.VesselId;
                requireVessel = true;
            }
            if (!String.IsNullOrWhiteSpace(vesselId))
                scope.Vessel = await ReadVesselAsync(database, effective, vesselId!, token).ConfigureAwait(false);
            if (scope.Vessel == null && (requireVessel || !String.IsNullOrWhiteSpace(vesselId)))
                throw new KeyNotFoundException("Vessel not found in the caller's scope.");
            return scope;
        }

        private static async Task<Mission?> ReadMissionAsync(DatabaseDriver database, AuthContext caller, string id, CancellationToken token)
        {
            CallerRecordScope scope = CallerScopedRead.ResolveScope(caller);
            if (scope.Kind != CallerRecordScopeKindEnum.User) return await CallerScopedRead.ReadMissionAsync(database, caller, id, token).ConfigureAwait(false);
            Mission? mission = await database.Missions.ReadAsync(scope.TenantId!, id, token).ConfigureAwait(false);
            return mission != null && OwnedByCaller(mission.UserId, scope) ? mission : null;
        }

        private static async Task<Vessel?> ReadVesselAsync(DatabaseDriver database, AuthContext caller, string id, CancellationToken token)
        {
            CallerRecordScope scope = CallerScopedRead.ResolveScope(caller);
            if (scope.Kind != CallerRecordScopeKindEnum.User) return await CallerScopedRead.ReadVesselAsync(database, caller, id, token).ConfigureAwait(false);
            Vessel? vessel = await database.Vessels.ReadAsync(scope.TenantId!, id, token).ConfigureAwait(false);
            return vessel != null && OwnedByCaller(vessel.UserId, scope) ? vessel : null;
        }

        // A record stored without a user belongs to the default user, the same user an ownerless caller acts as.
        private static bool OwnedByCaller(string? recordUserId, CallerRecordScope scope)
        {
            return String.Equals(OwnershipPolicy.UserOfRecord(recordUserId), scope.UserId, StringComparison.Ordinal);
        }
    }
}
