namespace Armada.Core.Models
{
    using System;
    using System.Collections.Generic;
    using Armada.Core.Enums;

    /// <summary>
    /// One incident linked to a mission, with its runbook executions.
    /// </summary>
    public class MissionRecoveryIncident
    {
        #region Public-Members

        /// <summary>
        /// Incident identifier.
        /// </summary>
        public string IncidentId { get; set; } = "";

        /// <summary>
        /// Incident title.
        /// </summary>
        public string Title { get; set; } = "";

        /// <summary>
        /// Incident status.
        /// </summary>
        public IncidentStatusEnum Status { get; set; } = IncidentStatusEnum.Open;

        /// <summary>
        /// Incident severity.
        /// </summary>
        public IncidentSeverityEnum Severity { get; set; } = IncidentSeverityEnum.High;

        /// <summary>
        /// Redacted and bounded recovery notes, or null.
        /// </summary>
        public string? RecoveryNotes { get; set; } = null;

        /// <summary>
        /// UTC detection time.
        /// </summary>
        public DateTime DetectedUtc { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// UTC mitigation time, or null.
        /// </summary>
        public DateTime? MitigatedUtc { get; set; } = null;

        /// <summary>
        /// UTC close time, or null.
        /// </summary>
        public DateTime? ClosedUtc { get; set; } = null;

        /// <summary>
        /// Runbook executions linked to the incident, newest first and bounded.
        /// </summary>
        public List<MissionRecoveryRunbookExecution> RunbookExecutions { get; set; } = new List<MissionRecoveryRunbookExecution>();

        /// <summary>
        /// Whether more runbook executions exist than are listed.
        /// </summary>
        public bool RunbookExecutionsTruncated { get; set; } = false;

        #endregion
    }
}
