namespace Armada.Core.Models
{
    /// <summary>Explicit correction of a planning-only administrative completion; never a restart.</summary>
    public sealed class AdministrativeVoyageRecoveryRequest
    {
        /// <summary>Return evidence and blockers without changing state.</summary>
        public bool DryRun { get; set; } = true;
        /// <summary>Current preview fingerprint required for apply.</summary>
        public string? ExpectedFingerprint { get; set; }
        /// <summary>Exact source commit that the planning stage started and ended on.</summary>
        public string SourceCommit { get; set; } = System.String.Empty;
        /// <summary>Optional exact-owner historical disposition for saved but unaccepted work.</summary>
        public HistoricalWorkDisposition? HistoricalWork { get; set; }
        /// <summary>Optional replacement description fixed by the issue preview.</summary>
        public string? RestartDescription { get; set; }
        /// <summary>Optional replacement title fixed by the issue preview.</summary>
        public string? RestartTitle { get; set; }
        /// <summary>Server-verified historical source and cancellation binding.</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public bool CapturedHistoricalEvidence { get; set; }
        /// <summary>Server-verified repository facts; clients cannot supply this evidence.</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public string? CapturedSourceEvidence { get; set; }
    }
}
