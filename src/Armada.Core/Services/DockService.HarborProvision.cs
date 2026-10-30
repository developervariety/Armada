namespace Armada.Core.Services
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Harbor;
    using Armada.Core.Models;
    using Armada.Core.Settings;

    /// <summary>Provisions explicitly selected Harbor-owned mission checkouts.</summary>
    public partial class DockService
    {
        private async Task<Dock?> TryProvisionHarborOwnedAsync(Vessel vessel, Captain captain, string branchName,
            string? missionId, bool detached, CancellationToken token)
        {
            HarborMissionRoute? route = HarborMissionRouting.ResolveConfiguredForCaptain(_Settings.Harbor, captain, vessel.Id);
            if (route?.CheckoutMode != HarborCheckoutModeEnum.HarborOwned) return null;
            if (!_Settings.Harbor.Enabled) throw new InvalidOperationException("dock_harbor_checkout_disabled");
            if (String.IsNullOrWhiteSpace(missionId)) throw new InvalidOperationException("dock_harbor_mission_missing");
            Mission? mission = await _Database.Missions.ReadAsync(missionId, token).ConfigureAwait(false);
            if (mission == null) throw new InvalidOperationException("dock_harbor_mission_missing");
            if (_HarborCheckouts == null) throw new InvalidOperationException("dock_harbor_checkout_unavailable");
            if (String.IsNullOrWhiteSpace(route.RunnerId) || String.IsNullOrWhiteSpace(route.RepositoryGrantId))
                throw new InvalidOperationException("dock_harbor_checkout_route_incomplete");
            if (!String.Equals(Armada.Core.Authorization.OwnershipPolicy.TenantOfRecord(mission.TenantId),
                    Armada.Core.Authorization.OwnershipPolicy.TenantOfRecord(vessel.TenantId), StringComparison.Ordinal)
                || !String.Equals(Armada.Core.Authorization.OwnershipPolicy.UserOfRecord(mission.UserId),
                    Armada.Core.Authorization.OwnershipPolicy.UserOfRecord(vessel.UserId), StringComparison.Ordinal)
                || !String.Equals(Armada.Core.Authorization.OwnershipPolicy.TenantOfRecord(captain.TenantId),
                    Armada.Core.Authorization.OwnershipPolicy.TenantOfRecord(mission.TenantId), StringComparison.Ordinal))
                throw new InvalidOperationException("dock_harbor_mission_owner_mismatch");
            if (String.IsNullOrWhiteSpace(mission.DockProvisionAttemptId))
                throw new InvalidOperationException("dock_harbor_provision_attempt_missing");
            foreach (SiblingRepo sibling in vessel.GetSiblingRepos())
            {
                if (sibling != null && sibling.BuildParticipant)
                    throw new InvalidOperationException("dock_harbor_required_sibling_unsupported: " + sibling.RelativePath);
            }
            Objective? siblingObjective = await ReadSiblingObjectiveAsync(vessel, mission.Id, token).ConfigureAwait(false);
            foreach (SiblingRepo sibling in vessel.GetSiblingRepos())
            {
                if (sibling != null && SiblingPinOverride.PinFor(siblingObjective, sibling) != null)
                    throw new InvalidOperationException("dock_harbor_sibling_pin_override_unsupported: " + sibling.RelativePath);
            }

            string requestedRevision = !String.IsNullOrWhiteSpace(mission.StartFromRef)
                ? mission.StartFromRef.Trim()
                : vessel.DefaultBranch;
            Dock dock = await _HarborCheckouts.ProvisionAsync(vessel, captain, branchName, route.RunnerId,
                route.RepositoryGrantId, requestedRevision, detached, mission.DockProvisionAttemptId, token).ConfigureAwait(false);
            if (!DockGitAnchorPersistence.IsCommit(dock.HostCheckout?.BaseCommit))
                throw new InvalidOperationException("dock_harbor_base_commit_unverified");
            dock.GitAnchorsSnapshot = new DockGitAnchorSnapshot
            {
                DockId = dock.Id,
                MissionId = mission.Id,
                VesselId = vessel.Id,
                ProvisionedCommit = dock.HostCheckout.BaseCommit,
                ProvisionedUtc = dock.CreatedUtc,
                Anchors = new GitAnchors { BaseCommit = dock.HostCheckout.BaseCommit, TargetBranch = vessel.DefaultBranch }
            };
            await _Database.Docks.UpdateAsync(dock, token).ConfigureAwait(false);
            ArmadaMetrics.RecordDockProvisioned();
            return dock;
        }

        private static HarborCheckoutBinding RequireHarborCheckout(Dock dock)
        {
            HarborCheckoutBinding? binding = dock.HostCheckout;
            if (binding == null || !binding.IsComplete
                || !String.Equals(binding.TenantId, dock.TenantId, StringComparison.Ordinal)
                || !String.Equals(binding.UserId, dock.UserId, StringComparison.Ordinal))
                throw new InvalidOperationException("dock_harbor_checkout_binding_invalid");
            return binding;
        }

        private async Task<string> ReadHarborHeadAsync(Dock dock, CancellationToken token)
        {
            RequireHarborCheckout(dock);
            HarborWorkspaceOperationRequest request = HarborCheckoutService.CreateRequest(dock, HarborWorkspaceOperationEnum.Git);
            request.Git = new HarborGitOperationRequest { Operation = HarborGitOperationEnum.GetHeadCommitHash };
            HarborWorkspaceOperationResult result = await RequireHarborCheckouts().ExecuteAsync(dock, request, token).ConfigureAwait(false);
            if (!DockGitAnchorPersistence.IsCommit(result.Git?.Text))
                throw new InvalidOperationException("dock_harbor_head_unverified");
            return result.Git!.Text!;
        }

        private HarborCheckoutService RequireHarborCheckouts()
        {
            if (!_Settings.Harbor.Enabled) throw new InvalidOperationException("dock_harbor_checkout_disabled");
            return _HarborCheckouts ?? throw new InvalidOperationException("dock_harbor_checkout_unavailable");
        }
    }
}
