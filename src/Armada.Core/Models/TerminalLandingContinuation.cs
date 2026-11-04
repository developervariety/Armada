namespace Armada.Core.Models
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// Operator grant that lets one Judge in a Failed voyage rerun at a regenerated candidate. The grant is a record
    /// only: it rewrites no status, counter or output, and it names the exact predecessors whose failure was
    /// reconciled as unlanded work.
    /// </summary>
    public sealed class TerminalLandingContinuation
    {
        /// <summary>Operator who granted the continuation.</summary>
        public string GrantedBy { get; set; } = String.Empty;

        /// <summary>UTC time of the grant.</summary>
        public DateTime GrantedUtc { get; set; }

        /// <summary>The Failed voyage the grant applies to.</summary>
        public string VoyageId { get; set; } = String.Empty;

        /// <summary>Each Failed predecessor the grant admits, with the status and commit read at grant time.</summary>
        public List<TerminalLandingContinuationPredecessor> Predecessors { get; set; } = new List<TerminalLandingContinuationPredecessor>();

        /// <summary>Preparation attempt whose candidate the grant last admitted to assignment; 0 before the first.</summary>
        public int AdmittedAttempt { get; set; }

        /// <summary>UTC time of the last admission.</summary>
        public DateTime? AdmittedUtc { get; set; }
    }

    /// <summary>A predecessor stage named by a terminal landing continuation.</summary>
    public sealed class TerminalLandingContinuationPredecessor
    {
        /// <summary>Predecessor mission.</summary>
        public string MissionId { get; set; } = String.Empty;

        /// <summary>Status read at grant time.</summary>
        public string Status { get; set; } = String.Empty;

        /// <summary>Commit read at grant time.</summary>
        public string CommitHash { get; set; } = String.Empty;
    }
}
