namespace Armada.Core.Services
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Harbor;
    using Armada.Core.Models;

    public partial class MergeQueueService
    {
        private async Task<string?> DescribeHarborUnpublishableCandidateAsync(MergeEntry entry, Dock integration,
            HarborQueueContext context, CancellationToken token)
        {
            string checkout = HarborCheckoutAddress.ForDock(integration.Id);
            string repository = checkout + "/repository";
            string? head = await _Git.GetHeadCommitHashAsync(checkout, token).ConfigureAwait(false);
            if (String.IsNullOrWhiteSpace(head)) return "the Harbor integration checkout has no readable candidate";
            if (_IntegrationValidator != null)
            {
                IntegrationValidationResult? evidence = await _IntegrationValidator.ReadLatestMergeEntryEvidenceAsync(entry.Id, token).ConfigureAwait(false);
                if (evidence == null || !evidence.AllowsPublication
                    || !String.Equals(evidence.Candidate.IntegrationCommit, head, StringComparison.OrdinalIgnoreCase))
                    return "the latest integration evidence does not authorize candidate " + head;
            }
            string? liveTarget = await InspectHarborRevisionAsync(context, "refs/heads/" + entry.TargetBranch, token).ConfigureAwait(false);
            if (String.IsNullOrWhiteSpace(liveTarget)) return "the remote target could not be read";
            if (!String.Equals(liveTarget, integration.HostCheckout?.BaseCommit, StringComparison.OrdinalIgnoreCase)
                && !String.Equals(liveTarget, head, StringComparison.OrdinalIgnoreCase))
                return "target branch moved from " + (integration.HostCheckout?.BaseCommit ?? "unknown") + " to " + liveTarget;
            string? baseHead = integration.HostCheckout?.BaseCommit;
            bool? containsBase = !String.IsNullOrWhiteSpace(baseHead)
                ? await _Git.TryIsAncestorAsync(repository, baseHead!, head!, token).ConfigureAwait(false)
                : null;
            if (containsBase != true) return "candidate no longer contains its pinned target base";
            return null;
        }

        private async Task LandHarborEntryAsync(MergeEntry entry, Dock integration,
            HarborQueueContext context, CancellationToken token)
        {
            string checkout = HarborCheckoutAddress.ForDock(integration.Id);
            string? head = await _Git.GetHeadCommitHashAsync(checkout, token).ConfigureAwait(false);
            if (String.IsNullOrWhiteSpace(head)) throw new InvalidOperationException("harbor_landing_candidate_head_unavailable");
            string? remote = await InspectHarborRevisionAsync(context, "refs/heads/" + entry.TargetBranch, token).ConfigureAwait(false);
            if (String.IsNullOrWhiteSpace(remote)) throw new InvalidOperationException("harbor_landing_target_unavailable");
            if (!String.Equals(remote, integration.HostCheckout?.BaseCommit, StringComparison.OrdinalIgnoreCase)
                && !String.Equals(remote, head, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("target branch drift: " + entry.TargetBranch + " moved before Harbor publication");

            using (IDisposable admission = await DirectLandingAdmission.AcquireAsync(_Database,
                context.Mission.TenantId, context.Mission.VoyageId, context.Mission.Id, token).ConfigureAwait(false))
            {
                if (await DirectLandingAdmission.IsCancelledAsync(_Database, context.Mission, token).ConfigureAwait(false))
                {
                    entry.Status = MergeStatusEnum.Cancelled;
                    entry.TestOutput = "Cancelled by operator";
                    entry.CompletedUtc = DateTime.UtcNow;
                    entry.LastUpdateUtc = DateTime.UtcNow;
                    await _Database.MergeEntries.UpdateAsync(entry, token).ConfigureAwait(false);
                    await UpdateLandingJobFromEntryAsync(entry, entry.TestOutput, token).ConfigureAwait(false);
                    return;
                }

                Mission? currentMission = await _Database.Missions.ReadAsync(context.Mission.Id, token).ConfigureAwait(false);
                Mission? boundJudge = currentMission == null ? null
                    : await GeneratedArtifactReviewResolver.ResolveJudgeAsync(_Database, currentMission, token).ConfigureAwait(false);
                if (boundJudge != null)
                {
                    string? currentTarget = await InspectHarborRevisionAsync(context, "refs/heads/" + entry.TargetBranch, token).ConfigureAwait(false);
                    string? currentSource = await InspectHarborRevisionAsync(context, "refs/heads/" + entry.BranchName, token).ConfigureAwait(false);
                    string? refusal = boundJudge.HeldForOperatorReview ? "generated_candidate_operator_hold"
                        : String.IsNullOrWhiteSpace(currentTarget) || String.IsNullOrWhiteSpace(currentSource)
                            ? "generated_candidate_source_unresolved"
                            : GeneratedArtifactReviewRule.PublicationRefusal(boundJudge, currentTarget, currentSource);
                    if (refusal != null)
                    {
                        await ParkGeneratedCandidateAsync(entry, context.Mission, refusal, token).ConfigureAwait(false);
                        return;
                    }
                }
                if (!String.Equals(remote, head, StringComparison.OrdinalIgnoreCase))
                {
                    using (new HarborCheckoutOperationScope("mq-push:" + entry.Id + ":" + head, head!))
                        await _Git.PushHeadToBranchAsync(checkout, "origin", entry.TargetBranch, token).ConfigureAwait(false);
                }
                string? landed = await InspectHarborRevisionAsync(context, "refs/heads/" + entry.TargetBranch, token).ConfigureAwait(false);
                if (!String.Equals(landed, head, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("harbor_landing_result_unverified: " + entry.TargetBranch);
                await SyncHarborLandingTargetAsync(entry, integration, head!, token).ConfigureAwait(false);
                entry.Status = MergeStatusEnum.Landed;
                entry.CompletedUtc = DateTime.UtcNow;
                entry.LastUpdateUtc = DateTime.UtcNow;
                await _Database.MergeEntries.UpdateAsync(entry, token).ConfigureAwait(false);
                await UpdateLandingJobFromEntryAsync(entry, null, token).ConfigureAwait(false);
                await ReconcileLandedBranchMissionsAsync(entry, "Landed via merge queue entry " + entry.Id, token).ConfigureAwait(false);
            }
            FireIndexRefreshForVessel(entry.VesselId);
            FireSelfDeployAfterLand(entry, context.Vessel);
        }

        private async Task<string?> InspectHarborRevisionAsync(HarborQueueContext context, string revision, CancellationToken token)
        {
            HarborWorkspaceOperationResult result = await (_HarborCheckouts
                ?? throw new InvalidOperationException("harbor_checkout_provider_unavailable"))
                .InspectRepositoryAsync(context.Vessel, context.Binding.RunnerId, context.Binding.RepositoryGrantId, revision, token).ConfigureAwait(false);
            if (!String.Equals(result.RepositoryFingerprint, context.Binding.RepositoryFingerprint, StringComparison.Ordinal))
                throw new InvalidOperationException("harbor_merge_repository_identity_changed");
            return result.HeadCommit;
        }
    }
}
