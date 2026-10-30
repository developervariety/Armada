namespace Armada.Core.Services
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Harbor;
    using Armada.Core.Models;

    public partial class LandingService
    {
        private async Task<bool> PublishHarborLandingAttemptAsync(Vessel vessel, Mission mission,
            HarborCheckoutBinding binding, HarborCheckoutService checkouts, string repository, string targetBranch, string branch,
            HarborLandingAttempt attempt, LandingMeasure measure, CancellationToken token)
        {
            string targetTip = attempt.TargetTip;
            string mergedHead = attempt.MergedHead;
            string attemptId = attempt.AttemptId;
            string integrationPath = HarborCheckoutAddress.ForDock(attempt.IntegrationDock.Id);
            string target = integrationPath + "/working";
            if (!String.Equals(mergedHead, targetTip, StringComparison.OrdinalIgnoreCase))
            {
                IntegrationValidationResult? validation = await ValidateCandidateAsync(
                    vessel, mission, targetBranch, branch, targetTip, mergedHead, token, repository).ConfigureAwait(false);
                if (validation != null && !validation.AllowsPublication)
                {
                    mission.FailureReason = validation.Reason;
                    mission.LastUpdateUtc = DateTime.UtcNow;
                    await PersistMissionRetryStateAsync(mission, token).ConfigureAwait(false);
                    await RemoveHarborIntegrationAsync(checkouts, attempt.IntegrationDock, attemptId, token).ConfigureAwait(false);
                    return false;
                }
            }

            HarborWorkspaceOperationResult observedTarget = await checkouts.InspectRepositoryAsync(vessel,
                binding.RunnerId, binding.RepositoryGrantId, "refs/heads/" + targetBranch, token).ConfigureAwait(false);
            if (!String.Equals(observedTarget.RepositoryFingerprint, binding.RepositoryFingerprint, StringComparison.Ordinal))
                throw new InvalidOperationException("harbor_landing_repository_identity_changed");
            string? latestTargetTip = observedTarget.HeadCommit;
            if (!String.Equals(latestTargetTip, targetTip, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("target branch drift: " + targetBranch + " moved from " + targetTip + " to " + (latestTargetTip ?? "(unresolved)"));

            if (!await PublishHarborReferencesAsync(vessel, mission, integrationPath, target, targetBranch,
                mergedHead, targetTip, branch, attemptId, token).ConfigureAwait(false))
            {
                measure.Outcome = ArmadaMetrics.OutcomeCanceled;
                await RemoveHarborIntegrationAsync(checkouts, attempt.IntegrationDock, attemptId, token).ConfigureAwait(false);
                return false;
            }

            string integrationRepository = integrationPath + "/repository";
            await FetchHarborRepositoryAsync(integrationRepository, mission.Id, token).ConfigureAwait(false);
            if (!await VerifyHarborWorkLandedAsync(integrationRepository, mission, targetBranch, token).ConfigureAwait(false))
                throw new InvalidOperationException("landing_result_unverified: " + targetBranch);
            bool synced = await SyncHarborWorkingDirectoryAsync(vessel, target, integrationRepository, targetBranch, attemptId, token).ConfigureAwait(false);
            if (!synced)
            {
                mission.FailureReason = "work_landed_post_step_failed: the work is on " + targetBranch
                    + " but the configured Harbor working checkout could not be synced";
                mission.LastUpdateUtc = DateTime.UtcNow;
                await PersistMissionRetryStateAsync(mission, token).ConfigureAwait(false);
                measure.Outcome = ArmadaMetrics.OutcomeLandedPostStepFailed;
            }
            await RemoveHarborIntegrationAsync(checkouts, attempt.IntegrationDock, attemptId, token).ConfigureAwait(false);
            return true;
        }

        private async Task<bool> PublishHarborReferencesAsync(Vessel vessel, Mission mission, string integrationPath,
            string target, string targetBranch, string mergedHead, string targetTip, string sourceBranch, string attemptId, CancellationToken token)
        {
            using (await DirectLandingAdmission.AcquireAsync(_Database, mission.TenantId,
                mission.VoyageId, mission.Id, token).ConfigureAwait(false))
            {
                if (await DirectLandingAdmission.IsCancelledAsync(_Database, mission, token).ConfigureAwait(false)) return false;
                Mission? current = await _Database.Missions.ReadAsync(mission.Id, token).ConfigureAwait(false);
                Mission? boundJudge = current == null ? null
                    : await GeneratedArtifactReviewResolver.ResolveJudgeAsync(_Database, current, token).ConfigureAwait(false);
                if (boundJudge != null)
                {
                    string? sourceTip = await _Git.GetRevisionCommitShaAsync(integrationPath + "/repository",
                        "refs/heads/" + sourceBranch, token).ConfigureAwait(false);
                    string? refusal = String.IsNullOrWhiteSpace(sourceTip) ? "generated_candidate_source_unresolved"
                        : boundJudge.HeldForOperatorReview ? "generated_candidate_operator_hold"
                        : GeneratedArtifactReviewRule.PublicationRefusal(boundJudge, targetTip, sourceTip);
                    if (refusal != null)
                    {
                        mission.FailureReason = refusal;
                        mission.LastUpdateUtc = DateTime.UtcNow;
                        await PersistMissionRetryStateAsync(mission, token).ConfigureAwait(false);
                        _Logging.Warn(_Header + "Harbor publication refused for " + mission.Id + ": " + refusal);
                        return false;
                    }
                }
                await PublishHarborReferencesWhileAdmittedAsync(vessel, integrationPath, target, targetBranch,
                    mergedHead, targetTip, attemptId, token).ConfigureAwait(false);
                return true;
            }
        }

        private async Task PublishHarborReferencesWhileAdmittedAsync(Vessel vessel, string integrationPath, string target,
            string targetBranch, string mergedHead, string targetTip, string attemptId, CancellationToken token)
        {
            if (vessel.LandingMode != LandingModeEnum.LocalMerge)
            {
                using (new HarborCheckoutOperationScope("landing-push:" + attemptId, mergedHead))
                    await _Git.PushHeadToBranchAsync(integrationPath, "origin", targetBranch, token).ConfigureAwait(false);
            }
            string repository = integrationPath + "/repository";
            string? targetHead = await _Git.GetHeadCommitHashAsync(repository, token).ConfigureAwait(false);
            if (String.IsNullOrWhiteSpace(targetHead)) throw new InvalidOperationException("harbor_primary_target_head_unavailable");
            using (new HarborCheckoutOperationScope("landing-cas:" + attemptId, targetHead!))
                await _Git.CompareAndSwapBranchRefAsync(repository, targetBranch, mergedHead, targetTip, token).ConfigureAwait(false);
        }
    }
}
