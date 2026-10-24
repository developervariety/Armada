namespace Armada.Server.Harbor
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;

    public sealed partial class DatabaseHarborWorkspaceBindingVerifier
    {
        private async Task<string?> ValidateMissionScopeAsync(AuthContext auth, Armada.Core.Harbor.HarborRunnerSession session, Vessel vessel, CancellationToken token)
        {
            if (String.IsNullOrWhiteSpace(auth.MissionId)) return null;
            Mission? mission = auth.IsAdmin
                ? await _Database.Missions.ReadAsync(auth.MissionId, token).ConfigureAwait(false)
                : await _Database.Missions.ReadAsync(auth.TenantId!, auth.MissionId, token).ConfigureAwait(false);
            if (mission == null || !String.Equals(mission.VesselId, vessel.Id, StringComparison.Ordinal)
                || !String.Equals(mission.TenantId, vessel.TenantId, StringComparison.Ordinal)) return "workspace_mission_scope_mismatch";
            if (!auth.IsAdmin && !auth.IsTenantAdmin && !String.Equals(mission.UserId, auth.UserId, StringComparison.Ordinal)) return "workspace_mission_scope_mismatch";
            if (!String.Equals(mission.TenantId, session.Identity.TenantId, StringComparison.Ordinal)
                || !String.Equals(mission.UserId, session.Identity.UserId, StringComparison.Ordinal)) return "workspace_mission_runner_owner_mismatch";
            return null;
        }
    }
}
