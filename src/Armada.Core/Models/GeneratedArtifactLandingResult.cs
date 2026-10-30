namespace Armada.Core.Models
{
    /// <summary>Outcome of preparing an isolated landing candidate; no target ref is advanced.</summary>
    public sealed class GeneratedArtifactLandingResult
    {
        /// <summary>Unchanged, Regenerated, Failed, or ReviewRequired.</summary>
        public string Outcome { get; set; } = "Unchanged";

        /// <summary>Named refusal or preparation reason.</summary>
        public string? Reason { get; set; }

        /// <summary>Target commit on which preparation began.</summary>
        public string? TargetBaseCommit { get; set; }

        /// <summary>Original reviewed source commit.</summary>
        public string? SourceCommit { get; set; }

        /// <summary>Prepared commit, when preparation succeeded.</summary>
        public string? CandidateCommit { get; set; }
    }
}
