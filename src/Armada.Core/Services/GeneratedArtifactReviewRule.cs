namespace Armada.Core.Services
{
    using System;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>Exact source and publication guards for a regenerated candidate's Judge review.</summary>
    public static class GeneratedArtifactReviewRule
    {
        /// <summary>A prepared candidate cannot release dependent stages until its exact review is approved.</summary>
        public static bool IsAwaitingApproval(Mission? mission) =>
            mission?.GeneratedArtifactReview != null && mission.GeneratedArtifactReview.ApprovedUtc == null;

        /// <summary>A live stage on the retained branch waits for normal completion instead of requiring an operator decision.</summary>
        public static bool IsTemporaryPreparationWait(string? reason) =>
            String.Equals(reason, "generated_candidate_branch_in_use", StringComparison.Ordinal);

        /// <summary>Build a bounded review binding without changing mission state or any recovery counter.</summary>
        public static GeneratedArtifactReviewBinding NextBinding(Mission mission, GeneratedArtifactLandingResult candidate,
            int maxLandingRetries, DateTime requestedUtc)
        {
            if (mission == null) throw new ArgumentNullException(nameof(mission));
            if (candidate == null) throw new ArgumentNullException(nameof(candidate));
            if (!String.Equals(mission.Persona, "Judge", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("generated_candidate_requires_judge");
            if (mission.HeldForOperatorReview)
                throw new InvalidOperationException("generated_candidate_operator_hold");
            if (candidate.Outcome != "ReviewRequired" || String.IsNullOrWhiteSpace(candidate.CandidateCommit)
                || String.IsNullOrWhiteSpace(candidate.SourceCommit) || String.IsNullOrWhiteSpace(candidate.TargetBaseCommit))
                throw new InvalidOperationException("generated_candidate_binding_incomplete");
            GeneratedArtifactReviewBinding? prior = mission.GeneratedArtifactReview;
            if (prior != null && prior.CandidateCommit == candidate.CandidateCommit && prior.ApprovedUtc == null)
                return prior;
            if (mission.Status != MissionStatusEnum.WorkProduced && mission.Status != MissionStatusEnum.Complete
                && mission.Status != MissionStatusEnum.LandingFailed)
                throw new InvalidOperationException("generated_candidate_review_active");
            if (!String.Equals(mission.CommitHash, candidate.SourceCommit, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("generated_candidate_source_changed");
            int attempt = checked((prior?.PreparationAttempt ?? 0) + 1);
            if (attempt > Math.Max(0, maxLandingRetries) + 1)
                throw new InvalidOperationException("generated_candidate_drift_limit");
            return new GeneratedArtifactReviewBinding
            {
                TargetBaseCommit = candidate.TargetBaseCommit,
                SourceCommit = candidate.SourceCommit,
                CandidateCommit = candidate.CandidateCommit,
                PreparationAttempt = attempt,
                RequestedUtc = requestedUtc,
                ApprovedUtc = null
            };
        }

        /// <summary>Refuse stale or unreviewed generated content before any publication path moves its target.</summary>
        public static string? PublicationRefusal(Mission mission, string currentTargetCommit, string currentSourceCommit)
        {
            if (mission == null) throw new ArgumentNullException(nameof(mission));
            GeneratedArtifactReviewBinding? binding = mission.GeneratedArtifactReview;
            if (binding == null) return null;
            if (binding.ApprovedUtc == null) return "generated_candidate_review_pending";
            if (!String.Equals(binding.CandidateCommit, currentSourceCommit, StringComparison.OrdinalIgnoreCase))
                return "generated_candidate_source_changed";
            if (!String.Equals(binding.TargetBaseCommit, currentTargetCommit, StringComparison.OrdinalIgnoreCase))
                return "generated_candidate_target_drift";
            return null;
        }
    }
}
