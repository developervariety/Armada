namespace Armada.Core.Models
{
    using System.Collections.Generic;

    /// <summary>Deterministic finding classification, never a Check or landing approval.</summary>
    public sealed class JudgeFindingTriageResult
    {
        /// <summary>Named format refusal, or null for valid findings.</summary>
        public string? Error { get; set; }
        /// <summary>Threshold applied to this exact report.</summary>
        public int Threshold { get; set; }
        /// <summary>Hash of the original report used for replay.</summary>
        public string? ReportSha256 { get; set; }
        /// <summary>Effective review verdict after the existing gates, retained for replay.</summary>
        public string? EffectiveVerdict { get; set; }
        /// <summary>Findings that remain blockers.</summary>
        public List<JudgeFinding> Blocking { get; set; } = new List<JudgeFinding>();
        /// <summary>Uncited findings below the threshold.</summary>
        public List<JudgeFinding> FollowUps { get; set; } = new List<JudgeFinding>();
        /// <summary>Whether the structured finding list is valid.</summary>
        public bool IsValid => Error == null;
        /// <summary>Whether the report may enter the existing PASS validation and Check gates.</summary>
        public bool EligibleForPassGates => IsValid && Blocking.Count == 0 && FollowUps.Count > 0;
    }
}
