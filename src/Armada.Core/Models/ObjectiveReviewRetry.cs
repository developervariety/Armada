namespace Armada.Core.Models
{
    using System;

    /// <summary>Operator evidence required to retry an unchanged-source review loop.</summary>
    public sealed class ObjectiveReviewRetry
    {
        /// <summary>The latest rejected Judge mission this receipt addresses.</summary>
        public string JudgeMissionId { get; set; } = String.Empty;
        /// <summary>The exact rejected candidate commit, preserved for review.</summary>
        public string CandidateHash { get; set; } = String.Empty;
        /// <summary>Hash of the new prepared input, as reported by dispatch preview.</summary>
        public string InputHash { get; set; } = String.Empty;
        /// <summary>The changed brief, evidence, or source input and why it fixes the rejection.</summary>
        public string ChangedInput { get; set; } = String.Empty;
        /// <summary>Evidence that addresses every named finding in the full rejected report.</summary>
        public string FindingsEvidence { get; set; } = String.Empty;
        /// <summary>Explicit operator attestation; the next Judge still verifies every finding.</summary>
        public bool AllFindingsAddressed { get; set; }
    }
}
