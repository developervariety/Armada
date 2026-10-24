namespace Armada.Server
{
    using System;
    using System.Security.Cryptography;
    using System.Text;
    using System.Threading.Tasks;
    using Armada.Core.Harbor;
    using Armada.Core.Models;
    using Armada.Core.Services;

    public partial class MissionLandingHandler
    {
        private async Task TrySyncHarborPullRequestTargetAsync(Mission mission, Vessel vessel, Dock sourceDock,
            HarborCheckoutBinding sourceBinding)
        {
            HarborCheckoutService? checkouts = _HarborCheckouts;
            if (checkouts == null) return;
            string targetBranch = String.IsNullOrWhiteSpace(vessel.DefaultBranch) ? "main" : vessel.DefaultBranch;
            string attemptId = HarborPullRequestSyncAttemptId(mission.Id, targetBranch);
            string branch = "armada/pr-sync/" + attemptId.Substring("prsync_".Length, 16);
            Dock? integration = null;
            try
            {
                integration = await checkouts.ProvisionAuxiliaryAsync(vessel, branch, sourceBinding.RunnerId,
                    sourceBinding.RepositoryGrantId, "refs/heads/" + targetBranch, true, attemptId, sourceDock.CaptainId).ConfigureAwait(false);
                string working = HarborCheckoutAddress.ForDock(integration.Id) + "/working";
                string? currentBranch = await _Git.GetCurrentBranchAsync(working).ConfigureAwait(false);
                if (!String.Equals(currentBranch, targetBranch, StringComparison.OrdinalIgnoreCase)
                    || await _Git.HasUncommittedTrackedChangesAsync(working).ConfigureAwait(false)) return;
                string? head = await _Git.GetHeadCommitHashAsync(working).ConfigureAwait(false);
                if (String.IsNullOrWhiteSpace(head)) return;
                using (new HarborCheckoutOperationScope("pr-sync:" + attemptId + ":" + head, head!))
                    await _Git.PullFastForwardOnlyAsync(working).ConfigureAwait(false);
                string? synced = await _Git.GetHeadCommitHashAsync(working).ConfigureAwait(false);
                HarborWorkspaceOperationResult target = await checkouts.InspectRepositoryAsync(vessel, sourceBinding.RunnerId,
                    sourceBinding.RepositoryGrantId, "refs/heads/" + targetBranch).ConfigureAwait(false);
                if (!String.Equals(target.RepositoryFingerprint, sourceBinding.RepositoryFingerprint, StringComparison.Ordinal)
                    || !String.Equals(synced, target.HeadCommit, StringComparison.OrdinalIgnoreCase))
                    _Logging.Warn(_Header + "PR reconciler: Harbor working checkout sync was not verified for mission " + mission.Id);
            }
            catch (Exception exception)
            {
                _Logging.Warn(_Header + "PR reconciler: Harbor working checkout sync failed for mission " + mission.Id + ": " + exception.Message);
            }
            finally
            {
                await RemovePrSyncCheckoutAsync(checkouts, integration, mission.Id, attemptId).ConfigureAwait(false);
            }
        }

        private async Task RemovePrSyncCheckoutAsync(HarborCheckoutService checkouts, Dock? integration,
            string missionId, string attemptId)
        {
            if (integration == null || !integration.Active || integration.HostCheckout == null) return;
            try
            {
                string checkout = HarborCheckoutAddress.ForDock(integration.Id);
                string? head = await _Git.GetHeadCommitHashAsync(checkout).ConfigureAwait(false);
                if (String.IsNullOrWhiteSpace(head)) return;
                HarborWorkspaceOperationRequest remove = HarborCheckoutService.CreateRequest(integration, HarborWorkspaceOperationEnum.RemoveCheckout);
                using (new HarborCheckoutOperationScope("pr-sync-remove:" + attemptId + ":" + head, head!))
                    await checkouts.ExecuteAsync(integration, remove).ConfigureAwait(false);
                integration.Active = false;
                await _Database.Docks.UpdateAsync(integration).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _Logging.Warn(_Header + "PR reconciler: Harbor sync checkout cleanup failed for mission " + missionId + ": " + exception.Message);
            }
        }

        private static string HarborPullRequestSyncAttemptId(string missionId, string targetBranch)
        {
            byte[] identity = Encoding.UTF8.GetBytes(missionId + "\n" + targetBranch);
            return "prsync_" + Convert.ToHexString(SHA256.HashData(identity)).ToLowerInvariant();
        }
    }
}
