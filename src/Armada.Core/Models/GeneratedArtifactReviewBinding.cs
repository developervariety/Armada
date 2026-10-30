namespace Armada.Core.Models
{
    using System;

    /// <summary>Server-owned source binding for a Judge reviewing a regenerated landing candidate.</summary>
    public sealed class GeneratedArtifactReviewBinding
    {
        /// <summary>Target commit used to prepare the candidate.</summary>
        public string TargetBaseCommit { get; set; } = String.Empty;

        /// <summary>Source commit approved before regeneration.</summary>
        public string SourceCommit { get; set; } = String.Empty;

        /// <summary>Exact regenerated commit requiring the same Judge's verdict.</summary>
        public string CandidateCommit { get; set; } = String.Empty;

        /// <summary>Bounded preparation count, separate from recovery and landing retry counters.</summary>
        public int PreparationAttempt { get; set; }

        /// <summary>UTC time the server requested the review.</summary>
        public DateTime RequestedUtc { get; set; }

        /// <summary>Set only after a verified Judge PASS at the exact candidate with its required Checks.</summary>
        public DateTime? ApprovedUtc { get; set; }
    }
}
