namespace Armada.Core.Services
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;

    public partial class LandingService
    {
        /// <summary>Shared mission lifecycle transition used to prepare and review a generated landing candidate.</summary>
        public Func<Mission, string?, CancellationToken, Task<GeneratedArtifactLandingResult>>? GeneratedArtifactPreparer { get; set; }

        private async Task<GeneratedArtifactLandingResult> PrepareGeneratedLandingAsync(Vessel vessel,
            Mission mission, string targetBranch, CancellationToken token)
        {
            if (GeneratedArtifactPreparer != null)
                return await GeneratedArtifactPreparer(mission, targetBranch, token).ConfigureAwait(false);
            return vessel.GeneratedArtifacts == null ? new GeneratedArtifactLandingResult()
                : new GeneratedArtifactLandingResult { Outcome = "Failed", Reason = "generated_artifacts_preparer_unavailable" };
        }

        private async Task<string?> RefuseUnapprovedGeneratedPublicationAsync(Vessel vessel, Mission mission,
            string targetBranch, string sourceBranch, CancellationToken token)
        {
            Mission? current = await _Database.Missions.ReadAsync(mission.Id, token).ConfigureAwait(false);
            if (current == null) return vessel.GeneratedArtifacts == null && mission.GeneratedArtifactReview == null
                ? null : "generated_candidate_mission_missing";
            Mission? judge = await GeneratedArtifactReviewResolver.ResolveJudgeAsync(_Database, current, token).ConfigureAwait(false);
            if (judge == null) return null;
            string? target = await _Git.GetRevisionCommitShaAsync(vessel.LocalPath!, "refs/heads/" + targetBranch, token).ConfigureAwait(false);
            string? source = await _Git.GetRevisionCommitShaAsync(vessel.LocalPath!, "refs/heads/" + sourceBranch, token).ConfigureAwait(false);
            if (target == null || source == null) return "generated_candidate_source_unresolved";
            if (judge.HeldForOperatorReview) return "generated_candidate_operator_hold";
            string? refusal = GeneratedArtifactReviewRule.PublicationRefusal(judge, target, source);
            if (refusal == "generated_candidate_target_drift" && judge.GeneratedArtifactReview?.ApprovedUtc != null
                && source == judge.GeneratedArtifactReview.CandidateCommit
                && await _Git.TryIsAncestorAsync(vessel.LocalPath!, source, target, token).ConfigureAwait(false) == true)
                return null;
            return refusal;
        }
    }
}
