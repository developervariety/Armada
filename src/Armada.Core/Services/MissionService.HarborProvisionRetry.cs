namespace Armada.Core.Services
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Harbor;
    using Armada.Core.Models;
    using Armada.Core.Settings;

    public partial class MissionService
    {
        private const string HarborProvisionAttemptUnavailableHold = "harbor_provision_attempt_unavailable";
        private const string HarborProvisionAttemptIdentityHold = "harbor_provision_attempt_identity";
        private const string HarborProvisionAttemptCaptainHold = "harbor_provision_attempt_waiting_for_captain";

        private async Task HoldFailedHarborProvisionAsync(
            Mission mission, Captain captain, Dock? dock, string cause, CancellationToken token)
        {
            bool changed = !String.Equals(mission.AssignmentHoldReason, HarborProvisionAttemptUnavailableHold, StringComparison.Ordinal)
                || !String.Equals(mission.AssignmentHoldEntityId, dock?.Id ?? mission.DockProvisionAttemptId, StringComparison.Ordinal);
            mission.Status = MissionStatusEnum.Pending;
            mission.AssignmentState = MissionAssignmentStateEnum.WaitingForDependency;
            mission.AssignmentHoldReason = HarborProvisionAttemptUnavailableHold;
            mission.AssignmentHoldEntityId = dock?.Id ?? mission.DockProvisionAttemptId;
            mission.CaptainId = null;
            if (dock != null)
            {
                mission.BranchName = dock.BranchName;
                mission.DockId = dock.Id;
            }
            mission.ProcessId = null;
            mission.ProcessStartedUtc = null;
            mission.LastUpdateUtc = DateTime.UtcNow;
            if (await WriteAssignmentAsync(mission, MissionStatusEnum.Assigned, token).ConfigureAwait(false)
                && changed && MissionAssignmentHold.IsNamedHold(HarborProvisionAttemptUnavailableHold))
            {
                await RecordAssignmentHoldEventAsync(mission, MissionAssignmentHold.HeldEventType,
                    HarborProvisionAttemptUnavailableHold, mission.AssignmentHoldEntityId,
                    "Mission " + mission.Id + " is held on its durable Harbor checkout attempt: " + cause,
                    token).ConfigureAwait(false);
            }
            await ReleaseClaimIfHeldAsync(captain.Id, mission.Id).ConfigureAwait(false);
        }

        private async Task<string?> ValidatePersistedHarborAttemptAsync(
            Mission mission, Vessel vessel, Dock dock, CancellationToken token)
        {
            HarborCheckoutBinding? binding = dock.HostCheckout;
            if (!dock.Active || !String.Equals(dock.ProvisionAttemptId, mission.DockProvisionAttemptId, StringComparison.Ordinal)
                || !String.Equals(dock.VesselId, vessel.Id, StringComparison.Ordinal)
                || !String.Equals(dock.TenantId, vessel.TenantId, StringComparison.Ordinal)
                || !String.Equals(dock.UserId, vessel.UserId, StringComparison.Ordinal)
                || String.IsNullOrWhiteSpace(dock.CaptainId) || String.IsNullOrWhiteSpace(dock.BranchName)
                || binding == null || !binding.IsComplete
                || !String.Equals(binding.TenantId, vessel.TenantId, StringComparison.Ordinal)
                || !String.Equals(binding.UserId, vessel.UserId, StringComparison.Ordinal)
                || !String.Equals(binding.BranchName, dock.BranchName, StringComparison.Ordinal))
                return "The saved Harbor checkout does not match this mission's owner, vessel, captain, branch, or provision attempt.";

            Captain? captain = await _Database.Captains.ReadAsync(dock.CaptainId, token).ConfigureAwait(false);
            if (captain == null || !String.Equals(captain.TenantId, mission.TenantId, StringComparison.Ordinal)
                || !String.Equals(captain.UserId, mission.UserId, StringComparison.Ordinal))
                return "The captain bound to the saved Harbor checkout is missing or no longer belongs to this mission owner.";
            if (_Settings.Harbor == null || !_Settings.Harbor.Enabled)
                return "Harbor is disabled while this mission still has a durable checkout attempt.";

            HarborMissionRoute? route = HarborMissionRouting.ResolveConfigured(_Settings.Harbor, captain, mission);
            if (route?.CheckoutMode != HarborCheckoutModeEnum.HarborOwned
                || !String.Equals(route.RunnerId, binding.RunnerId, StringComparison.Ordinal)
                || !String.Equals(route.RepositoryGrantId, binding.RepositoryGrantId, StringComparison.Ordinal))
                return "The current explicit Harbor route no longer matches the saved runner and repository grant.";
            return null;
        }
    }
}
