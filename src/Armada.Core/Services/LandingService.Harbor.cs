namespace Armada.Core.Services
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Harbor;
    using Armada.Core.Models;

    public partial class LandingService
    {
        private async Task<bool> MergeInHarborDedicatedWorktreeAsync(Vessel vessel, Mission mission, Dock dock,
            string targetBranch, string? sourceBranch, string? commitMessage, LandingMeasure measure, CancellationToken token)
        {
            HarborCheckoutBinding? binding = dock.HostCheckout;
            HarborCheckoutService checkouts = _HarborCheckouts
                ?? throw new InvalidOperationException("harbor_checkout_provider_unavailable");
            if (binding == null || !binding.IsComplete || !dock.Active
                || !String.Equals(mission.DockId, dock.Id, StringComparison.Ordinal)
                || !String.Equals(mission.VesselId, dock.VesselId, StringComparison.Ordinal)
                || !String.Equals(vessel.Id, dock.VesselId, StringComparison.Ordinal)
                || !String.Equals(mission.TenantId, dock.TenantId, StringComparison.Ordinal)
                || !String.Equals(mission.UserId, dock.UserId, StringComparison.Ordinal))
                throw new InvalidOperationException("harbor_landing_dock_binding_invalid");
            if (String.IsNullOrWhiteSpace(targetBranch)) throw new InvalidOperationException("landing_target_branch_required");

            if (vessel.GeneratedArtifacts != null)
            {
                mission.FailureReason = "generated_artifacts_harbor_unsupported: generated artifacts require a native owned worktree";
                mission.LastUpdateUtc = DateTime.UtcNow;
                await PersistMissionRetryStateAsync(mission, token).ConfigureAwait(false);
                _Logging.Warn(_Header + "refusing Harbor generated-artifact landing for " + mission.Id + ": " + mission.FailureReason);
                return false;
            }

            string branch = !String.IsNullOrWhiteSpace(sourceBranch) ? sourceBranch! : mission.BranchName ?? dock.BranchName ?? String.Empty;
            if (String.IsNullOrWhiteSpace(branch)) throw new InvalidOperationException("landing_source_branch_required");
            string source = HarborCheckoutAddress.ForDock(dock.Id);
            string repository = source + "/repository";
            measure.Attempted = true;
            int maxRetries = Math.Max(0, _Settings.MaxLandingRetries);

            while (true)
            {
                token.ThrowIfCancellationRequested();
                Dock? integration = null;
                try
                {
                    HarborLandingAttempt attempt = await PrepareHarborLandingAttemptAsync(vessel, mission, dock, binding,
                        repository, targetBranch, branch, commitMessage, created => integration = created, token).ConfigureAwait(false);
                    integration = attempt.IntegrationDock;
                    if (await PublishHarborLandingAttemptAsync(vessel, mission, binding, checkouts,
                        repository, targetBranch, branch, attempt, measure, token).ConfigureAwait(false)) return true;
                    return false;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception) when (IsTargetBranchDrift(exception) && mission.LandingRetryCount < maxRetries)
                {
                    if (integration != null)
                        await RemoveHarborIntegrationAsync(checkouts, integration,
                            integration.ProvisionAttemptId ?? HarborLandingAttemptId(mission.Id, mission.LandingRetryCount, integration.HostCheckout!.BaseCommit), token).ConfigureAwait(false);
                    measure.Retried = true;
                    mission.LandingRetryCount++;
                    mission.LastUpdateUtc = DateTime.UtcNow;
                    await PersistMissionRetryStateAsync(mission, token).ConfigureAwait(false);
                    _Logging.Warn(_Header + "Harbor target drift for mission " + mission.Id + "; retrying landing attempt " + mission.LandingRetryCount);
                }
                catch (Exception exception)
                {
                    mission.FailureReason = "harbor_landing_failed: " + exception.Message;
                    mission.LastUpdateUtc = DateTime.UtcNow;
                    await PersistMissionRetryStateAsync(mission, token).ConfigureAwait(false);
                    _Logging.Warn(_Header + "Harbor landing failed for mission " + mission.Id + ": " + exception.Message);
                    return false;
                }
            }
        }

    }
}
