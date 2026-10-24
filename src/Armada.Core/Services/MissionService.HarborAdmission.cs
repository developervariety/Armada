namespace Armada.Core.Services
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Settings;

    /// <summary>Harbor capacity admission for mission assignment.</summary>
    public partial class MissionService
    {
        private static bool IsHarborCapacityHold(Mission mission)
        {
            return String.Equals(mission.AssignmentHoldReason, MissionAssignmentHold.HarborRunnerCapacity, StringComparison.Ordinal);
        }

        private async Task<bool> TryHoldForHarborCapacityAsync(Mission mission, HarborMissionRoute? route, CancellationToken token)
        {
            Func<AuthContext, string, CancellationToken, Task<bool>>? probe = HarborCapacityProbe;
            if (probe == null || route == null
                || String.IsNullOrWhiteSpace(mission.TenantId) || String.IsNullOrWhiteSpace(mission.UserId)) return false;

            AuthContext owner = AuthContext.Authenticated(mission.TenantId, mission.UserId, false, false, "HarborMission", null);
            string runnerId = route.RunnerId.Trim();
            if (!await probe(owner, runnerId, token).ConfigureAwait(false)) return false;

            await HoldForAssignmentAsync(mission, MissionAssignmentHold.HarborRunnerCapacity, runnerId,
                "The configured Harbor runner has no free job slot.", token).ConfigureAwait(false);
            return true;
        }
    }
}
