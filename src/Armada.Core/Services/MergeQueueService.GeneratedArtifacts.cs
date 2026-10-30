namespace Armada.Core.Services
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    public partial class MergeQueueService
    {
        /// <summary>Shared mission lifecycle transition for preparing generated candidates on the actual target.</summary>
        public Func<Mission, string?, CancellationToken, Task<GeneratedArtifactLandingResult>>? GeneratedArtifactPreparer { get; set; }

        private async Task<bool> ParkUnapprovedGeneratedCandidateAsync(MergeEntry entry, Mission mission,
            string repository, CancellationToken token)
        {
            Mission? judge = await GeneratedArtifactReviewResolver.ResolveJudgeAsync(_Database, mission, token).ConfigureAwait(false);
            if (judge == null) return false;
            if (judge.GeneratedArtifactReview?.ApprovedUtc == null
                && (judge.Status == MissionStatusEnum.Failed || judge.Status == MissionStatusEnum.Cancelled))
            {
                await TransitionEntryToFailureAsync(entry, "generated_candidate_review_rejected", token, fireRecovery: false).ConfigureAwait(false);
                return true;
            }
            string? target = await TryResolveCommitAsync(repository, "refs/heads/" + entry.TargetBranch, token).ConfigureAwait(false);
            string? source = await TryResolveCommitAsync(repository, "refs/heads/" + entry.BranchName, token).ConfigureAwait(false);
            if (target == null || source == null) return false;
            string? refusal = judge.HeldForOperatorReview ? "generated_candidate_operator_hold"
                : GeneratedArtifactReviewRule.PublicationRefusal(judge, target, source);
            if (refusal == "generated_candidate_target_drift" && judge.GeneratedArtifactReview?.ApprovedUtc != null
                && source == judge.GeneratedArtifactReview.CandidateCommit
                && await _Git.TryIsAncestorAsync(repository, source, target, token).ConfigureAwait(false) == true)
                refusal = null;
            if (refusal != null && !judge.HeldForOperatorReview && judge.GeneratedArtifactReview?.ApprovedUtc != null
                && GeneratedArtifactPreparer != null
                && (refusal == "generated_candidate_target_drift" || refusal == "generated_candidate_source_changed"))
            {
                GeneratedArtifactLandingResult prepared = await GeneratedArtifactPreparer(mission, entry.TargetBranch, token).ConfigureAwait(false);
                if (prepared.Outcome == "Failed")
                {
                    if (!GeneratedArtifactReviewRule.IsTemporaryPreparationWait(prepared.Reason)
                        && prepared.Reason != "generated_candidate_operator_hold")
                    {
                        await TransitionEntryToFailureAsync(entry, prepared.Reason ?? "generated_preparation_failed", token,
                            fireRecovery: false).ConfigureAwait(false);
                        return true;
                    }
                    refusal += ": " + prepared.Reason;
                }
            }
            if (refusal == null)
            {
                if (entry.TestOutput?.StartsWith("generated_candidate_", StringComparison.Ordinal) == true)
                {
                    entry.TestOutput = null;
                    await _Database.MergeEntries.UpdateAsync(entry, token).ConfigureAwait(false);
                }
                return false;
            }
            await ParkGeneratedCandidateAsync(entry, mission, refusal, token).ConfigureAwait(false);
            return true;
        }

        private async Task ParkGeneratedCandidateAsync(MergeEntry entry, Mission mission, string refusal, CancellationToken token)
        {
            if (entry.Status != MergeStatusEnum.Queued || !String.Equals(entry.TestOutput, refusal, StringComparison.Ordinal))
            {
                entry.Status = MergeStatusEnum.Queued;
                entry.TestOutput = refusal;
                entry.CompletedUtc = null;
                entry.LastUpdateUtc = DateTime.UtcNow;
                await _Database.MergeEntries.UpdateAsync(entry, token).ConfigureAwait(false);
                await UpdateLandingJobFromEntryAsync(entry, refusal, token).ConfigureAwait(false);
                ArmadaEvent waitEvent = new ArmadaEvent("merge_queue.generated_candidate_wait", refusal)
                {
                    TenantId = entry.TenantId, EntityId = entry.Id, MissionId = mission.Id,
                    VesselId = mission.VesselId, VoyageId = mission.VoyageId
                };
                EventOwnerScope.ApplyFromMission(waitEvent, mission);
                await _Database.Events.CreateAsync(waitEvent, token).ConfigureAwait(false);
                _Logging.Info(_Header + "merge entry " + entry.Id + " waits: " + refusal);
            }
        }
    }
}
