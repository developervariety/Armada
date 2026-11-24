namespace Armada.Core.Services
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    public partial class DefinitionOfDoneGate
    {
        private static readonly AsyncLocal<Mission?> _AutomaticPermissionMission = new AsyncLocal<Mission?>();

        private IDisposable EnterAutomaticPermissionScope(Mission mission)
        {
            Mission? previous = _AutomaticPermissionMission.Value;
            _AutomaticPermissionMission.Value = mission;
            return new BudgetScope(() => _AutomaticPermissionMission.Value = previous);
        }

        private async Task RequireAutomaticVerificationPermissionAsync(CancellationToken token)
        {
            Mission? expected = _AutomaticPermissionMission.Value;
            if (expected == null) return;
            Mission? current = await _Database.Missions.ReadAsync(expected.Id, token).ConfigureAwait(false);
            if (expected.AutomaticWorkPermission == null && !expected.AutomaticWorkPermissionInvalid
                && current?.AutomaticWorkPermission == null && current?.AutomaticWorkPermissionInvalid != true) return;
            if (current == null || !String.Equals(expected.TenantId, current.TenantId, StringComparison.Ordinal)
                || !String.Equals(expected.UserId, current.UserId, StringComparison.Ordinal)
                || expected.StartedUtc != current.StartedUtc
                || !String.Equals(expected.CommitHash, current.CommitHash, StringComparison.Ordinal))
                throw new MissionAutomaticWorkPermissionRequiredException(expected.Id, "automatic_work_permission_binding_changed");
            if (!MissionAutomaticWorkPermissionPolicy.IsAllowed(current, MissionAutomaticActionEnum.Verification,
                DateTime.UtcNow, out string? reason))
                throw new MissionAutomaticWorkPermissionRequiredException(expected.Id, reason!);
        }
    }
}
