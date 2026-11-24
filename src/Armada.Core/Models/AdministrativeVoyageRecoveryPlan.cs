namespace Armada.Core.Models
{
    using System;
    using System.Collections.Generic;
    /// <summary>Evidence-bound preview or receipt for a nonlaunching administrative correction.</summary>
    public sealed class AdministrativeVoyageRecoveryPlan
    {
        /// <summary>Original voyage identifier.</summary>
        public string VoyageId { get; set; } = String.Empty;
        /// <summary>Digest of the complete prior rows and verified repository evidence.</summary>
        public string Fingerprint { get; set; } = String.Empty;
        /// <summary>Finite unresolved conditions; apply refuses while any remain.</summary>
        public List<string> Blockers { get; set; } = new List<string>();
        /// <summary>Server-verified source evidence, or the exact missing proof.</summary>
        public string? SourceEvidence { get; set; }
        /// <summary>Whether the correction was committed by this call.</summary>
        public bool Applied { get; set; }
        /// <summary>Audit event that retains the original Complete outcome and all prior rows.</summary>
        public string? AuditEventId { get; set; }
        /// <summary>One-use restart permit, issued without starting a mission.</summary>
        public string? RestartPermitId { get; set; }
    }
}
