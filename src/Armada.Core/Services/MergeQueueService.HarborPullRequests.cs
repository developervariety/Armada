namespace Armada.Core.Services
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Harbor;
    using Armada.Core.Models;

    public partial class MergeQueueService
    {
        private async Task<bool> OpenHarborPullRequestAsync(MergeEntry entry, Dock integration, CancellationToken token)
        {
            if (!String.IsNullOrWhiteSpace(entry.PrUrl))
            {
                await MarkExistingPullRequestOpenAsync(entry, token).ConfigureAwait(false);
                await RemoveHarborIntegrationAsync(entry, integration, token).ConfigureAwait(false);
                return true;
            }
            Mission? mission = !String.IsNullOrWhiteSpace(entry.TenantId)
                ? await _Database.Missions.ReadAsync(entry.TenantId!, entry.MissionId!, token).ConfigureAwait(false)
                : await _Database.Missions.ReadAsync(entry.MissionId!, token).ConfigureAwait(false);
            if (mission == null)
            {
                await TransitionEntryToFailureAsync(entry, "harbor_pr_mission_unavailable", token).ConfigureAwait(false);
                await RemoveHarborIntegrationAsync(entry, integration, token).ConfigureAwait(false);
                return false;
            }
            string checkout = HarborCheckoutAddress.ForDock(integration.Id);
            string repository = checkout + "/repository";
            string? repositoryHead = await _Git.GetHeadCommitHashAsync(repository, token).ConfigureAwait(false);
            if (String.IsNullOrWhiteSpace(repositoryHead)) throw new InvalidOperationException("harbor_pr_repository_head_unavailable");
            LandingJob job = await EnsureLandingJobAsync(entry, token).ConfigureAwait(false);
            using (new HarborCheckoutOperationScope("mq-pr-push:" + HarborQueueAttemptId(entry.Id, job.RetryCount) + ":" + repositoryHead, repositoryHead!))
                await _Git.PushRefSpecAsync(repository, entry.BranchName, entry.BranchName, token).ConfigureAwait(false);
            string body = "Auto-routed to PR review by Armada merge queue.\n\nCritical trigger: " + entry.AuditCriticalTrigger
                + "\nMission: " + mission.Id + "\nVessel: " + (await ReadVesselNameAsync(entry, token).ConfigureAwait(false));
            string? currentHead = await _Git.GetHeadCommitHashAsync(checkout, token).ConfigureAwait(false);
            if (String.IsNullOrWhiteSpace(currentHead)) throw new InvalidOperationException("harbor_pr_checkout_head_unavailable");
            string prUrl;
            using (new HarborCheckoutOperationScope("mq-pr-create:" + HarborQueueAttemptId(entry.Id, job.RetryCount) + ":" + currentHead, currentHead!))
                prUrl = await _Git.CreatePullRequestAsync(checkout, mission.Title, body, entry.BranchName!, entry.TargetBranch!, token).ConfigureAwait(false);
            entry.PrUrl = prUrl;
            entry.PrBaseBranch = entry.TargetBranch;
            entry.Status = Armada.Core.Enums.MergeStatusEnum.PullRequestOpen;
            entry.LastUpdateUtc = DateTime.UtcNow;
            await _Database.MergeEntries.UpdateAsync(entry, token).ConfigureAwait(false);
            await UpdateLandingJobFromEntryAsync(entry, null, token).ConfigureAwait(false);
            mission.Status = Armada.Core.Enums.MissionStatusEnum.PullRequestOpen;
            mission.PrUrl = prUrl;
            mission.LastUpdateUtc = DateTime.UtcNow;
            await _Database.Missions.UpdateAsync(mission, token).ConfigureAwait(false);
            await RemoveHarborIntegrationAsync(entry, integration, token).ConfigureAwait(false);
            return true;
        }

        private async Task<string> ReadVesselNameAsync(MergeEntry entry, CancellationToken token)
        {
            Vessel? vessel = !String.IsNullOrWhiteSpace(entry.TenantId)
                ? await _Database.Vessels.ReadAsync(entry.TenantId!, entry.VesselId!, token).ConfigureAwait(false)
                : await _Database.Vessels.ReadAsync(entry.VesselId!, token).ConfigureAwait(false);
            return vessel?.Name ?? "unknown";
        }
    }
}
