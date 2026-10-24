namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Text.RegularExpressions;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Harbor;
    using Armada.Core.Models;
    using Armada.Core.Settings;

    public partial class CheckRunService
    {
        private async Task<HarborCheckExecution?> TryRunHarborCheckAsync(
            CheckRun run, Vessel vessel, WorkflowProfile? profile,
            string command, bool commandOverride, CheckCommandBudget budget, CancellationToken token)
        {
            HarborCheckRoute? route = await ResolveHarborCheckRouteAsync(run, vessel, token).ConfigureAwait(false);
            if (route == null) return null;
            if (HarborSettings == null || !HarborSettings.Enabled || HarborCheckouts == null)
                throw new InvalidOperationException("harbor_owned_check_unavailable: Harbor is disabled or its checkout channel is not ready.");
            if (commandOverride)
                throw new InvalidOperationException("harbor_owned_check_command_override_unsupported");
            string runnerId = route.RunnerId;
            string grantId = route.RepositoryGrantId;
            if (String.IsNullOrWhiteSpace(runnerId) || String.IsNullOrWhiteSpace(grantId))
                throw new InvalidOperationException("harbor_owned_check_route_invalid");
            Dictionary<string, string> commandEnvironment = await BuildCommandEnvironmentAsync(profile, run, token).ConfigureAwait(false);
            if (commandEnvironment.Count != 0)
                throw new InvalidOperationException("harbor_owned_check_profile_environment_unsupported");
            string requestedRevision = !String.IsNullOrWhiteSpace(run.CommitHash) ? run.CommitHash!
                : !String.IsNullOrWhiteSpace(run.BranchName) ? run.BranchName!
                : vessel.DefaultBranch ?? "HEAD";
            string branchName = !String.IsNullOrWhiteSpace(run.BranchName) ? run.BranchName!
                : vessel.DefaultBranch ?? ("check-" + run.Id);
            int timeoutSeconds = budget.GetRemoteTimeoutSeconds();
            Dock dock = await HarborCheckouts.ProvisionAuxiliaryAsync(vessel, branchName, runnerId, grantId,
                requestedRevision, true, "check:" + run.Id + ":owned", token: token).ConfigureAwait(false);
            HarborCheckoutBinding binding = dock.HostCheckout
                ?? throw new InvalidOperationException("harbor_owned_check_binding_missing");
            string commandKey = HarborVerificationProtocol.CheckCommandKey(run.Type);
            string commandHash = HarborVerificationProtocol.CommandSha256(command);
            HarborWorkspaceOperationRequest operation = HarborCheckoutService.CreateRequest(dock, HarborWorkspaceOperationEnum.RunVerification);
            operation.Verification = new HarborVerificationRequest
            {
                CommandKey = commandKey,
                ExpectedCommandSha256 = commandHash,
                CheckId = run.Id,
                TimeoutSeconds = timeoutSeconds,
                OutputLimitBytes = CheckCommandOutputLimitBytes,
                AcceptedExitCodes = new List<int> { 0 }
            };
            using (HarborCheckoutOperationScope scope = new HarborCheckoutOperationScope("check:" + run.Id + ":owned-run", binding.BaseCommit))
            {
                HarborWorkspaceOperationResult completed = await HarborCheckouts.ExecuteAsync(dock, operation, token).ConfigureAwait(false);
                budget.RecordRemoteExecution(completed);
                HarborVerificationOutputText output = await HarborVerificationOutputReader.ReadAsync(
                    HarborCheckouts, dock, completed, run.Id, binding.BaseCommit, token).ConfigureAwait(false);
                return new HarborCheckExecution
                {
                    Dock = dock,
                    Binding = binding,
                    Result = completed,
                    Output = CombineOutput(output.StandardOutput, output.StandardError)
                        + (budget.FailureReason == null ? String.Empty : Environment.NewLine + budget.FailureReason),
                    WorkingDirectory = HarborCheckoutAddress.ForDock(dock.Id) + "/checkout/" + binding.CheckoutId
                };
            }
        }

        private async Task<bool> IsHarborCheckSelectedAsync(CheckRun run, Vessel vessel, CancellationToken token)
        {
            return await ResolveHarborCheckRouteAsync(run, vessel, token).ConfigureAwait(false) != null;
        }

        private async Task<HarborCheckRoute?> ResolveHarborCheckRouteAsync(CheckRun run, Vessel vessel, CancellationToken token)
        {
            if (!String.IsNullOrWhiteSpace(run.MissionId))
            {
                Mission? mission = await _Database.Missions.ReadAsync(run.TenantId!, run.MissionId!, token).ConfigureAwait(false);
                if (mission != null && !String.IsNullOrWhiteSpace(mission.DockId))
                {
                    Dock? sourceDock = await _Database.Docks.ReadAsync(mission.DockId!, token).ConfigureAwait(false);
                    if (sourceDock != null && sourceDock.Active && sourceDock.HostCheckout != null
                        && sourceDock.TenantId == run.TenantId && sourceDock.UserId == run.UserId && sourceDock.VesselId == vessel.Id)
                        return new HarborCheckRoute(sourceDock.HostCheckout.RunnerId, sourceDock.HostCheckout.RepositoryGrantId);
                }
            }
            HarborMissionRoute? configured = HarborMissionRouting.ResolveConfiguredForVessel(HarborSettings, vessel.Id);
            if (configured?.CheckoutMode != HarborCheckoutModeEnum.HarborOwned) return null;
            if (String.IsNullOrWhiteSpace(configured.RepositoryGrantId))
                throw new InvalidOperationException("harbor_owned_check_grant_missing");
            return new HarborCheckRoute(configured.RunnerId, configured.RepositoryGrantId!);
        }

    }
}
