namespace Armada.Core.Models
{
    using System;
    /// <summary>Durable, exact-owner, source-bound permission for one explicit same-mission restart.</summary>
    public sealed class AdministrativeRestartPermit
    {
        /// <summary>Event identifier of the issued permit.</summary>
        public string PermitId { get; set; } = String.Empty;
        /// <summary>Exact tenant.</summary>
        public string TenantId { get; set; } = String.Empty;
        /// <summary>Exact owner.</summary>
        public string UserId { get; set; } = String.Empty;
        /// <summary>Original voyage.</summary>
        public string VoyageId { get; set; } = String.Empty;
        /// <summary>Only mission this permit may restart.</summary>
        public string MissionId { get; set; } = String.Empty;
        /// <summary>Prior complete graph fingerprint.</summary>
        public string PriorFingerprint { get; set; } = String.Empty;
        /// <summary>Exact corrected graph and source evidence, required unchanged at consumption.</summary>
        public string CorrectedFingerprint { get; set; } = String.Empty;
        /// <summary>Exact source evidence captured and checked at issue and consumption.</summary>
        public string SourceEvidence { get; set; } = String.Empty;
        /// <summary>Immutable owner disposition, separate from server proof.</summary>
        public HistoricalWorkDisposition Disposition { get; set; } = new HistoricalWorkDisposition();
        /// <summary>Planning source commit.</summary>
        public string PlanningSourceCommit { get; set; } = String.Empty;
        /// <summary>Optional instructions fixed at issue, not selected after the fingerprint was approved.</summary>
        public string? RestartDescription { get; set; }
        /// <summary>Optional title fixed at issue.</summary>
        public string? RestartTitle { get; set; }
    }
}
