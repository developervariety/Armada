namespace Armada.Core.Services
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;

    public partial class MissionService
    {
        private async Task<bool> VerifyGeneratedCandidateBeforeAssignmentAsync(Mission mission, Vessel vessel,
            string? predecessorCommit, bool sameOwner, bool crossVessel, CancellationToken token)
        {
            GeneratedArtifactReviewBinding? binding = mission.GeneratedArtifactReview;
            if (binding == null) return true;
            string? refusal = null;
            if (!String.Equals(mission.Persona, "Judge", StringComparison.OrdinalIgnoreCase)
                || binding.ApprovedUtc != null || String.IsNullOrWhiteSpace(binding.CandidateCommit)
                || !String.Equals(mission.CommitHash, binding.CandidateCommit, StringComparison.Ordinal)
                || !String.Equals(mission.StartFromRef, binding.CandidateCommit, StringComparison.Ordinal)
                || String.IsNullOrWhiteSpace(mission.BranchName))
                refusal = "generated_candidate_assignment_binding_invalid";
            else if (_Git == null || String.IsNullOrWhiteSpace(vessel.LocalPath))
                refusal = "generated_candidate_assignment_source_unavailable";
            else if (!String.IsNullOrWhiteSpace(mission.DependsOnMissionId)
                && (!sameOwner || crossVessel || String.IsNullOrWhiteSpace(predecessorCommit)))
                refusal = "generated_candidate_predecessor_unverified";
            else
            {
                try
                {
                    string? branchHead = await _Git.GetRevisionCommitShaAsync(vessel.LocalPath,
                        "refs/heads/" + mission.BranchName, token).ConfigureAwait(false);
                    if (!String.Equals(branchHead, binding.CandidateCommit, StringComparison.Ordinal)
                        && !String.IsNullOrWhiteSpace(branchHead)
                        && mission.Status == Armada.Core.Enums.MissionStatusEnum.Pending && !mission.HeldForOperatorReview
                        && await _Git.TryIsAncestorAsync(vessel.LocalPath, branchHead, binding.CandidateCommit, token).ConfigureAwait(false) == true)
                    {
                        await FinishGeneratedCandidateBranchAsync(vessel, mission, branchHead, token).ConfigureAwait(false);
                        branchHead = await _Git.GetRevisionCommitShaAsync(vessel.LocalPath,
                            "refs/heads/" + mission.BranchName, token).ConfigureAwait(false);
                    }
                    if (!String.Equals(branchHead, binding.CandidateCommit, StringComparison.Ordinal))
                        refusal = "generated_candidate_branch_changed";
                    else if (!String.IsNullOrWhiteSpace(predecessorCommit)
                        && await _Git.TryIsAncestorAsync(vessel.LocalPath, predecessorCommit,
                            binding.CandidateCommit, token).ConfigureAwait(false) != true)
                        refusal = "generated_candidate_predecessor_missing";
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    _Logging.Warn(_Header + "candidate assignment proof unavailable for " + mission.Id + ": " + ex.GetType().Name);
                    refusal = "generated_candidate_assignment_source_unavailable";
                }
            }
            if (refusal == null) return true;
            await HoldForAssignmentAsync(mission, refusal, mission.Id,
                "The regenerated Judge candidate cannot launch until its exact branch and predecessor ancestry are verified.", token).ConfigureAwait(false);
            return false;
        }
    }
}
