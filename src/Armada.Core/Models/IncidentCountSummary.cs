namespace Armada.Core.Models
{
    using System.Collections.Generic;
    using Armada.Core.Enums;

    /// <summary>
    /// Incident counts by status, and open incident counts by severity, taken from one read of the
    /// incidents a caller can see. A value with no incidents has no entry.
    /// </summary>
    public class IncidentCountSummary
    {
        #region Public-Members

        /// <summary>
        /// Incident count per status.
        /// </summary>
        public Dictionary<IncidentStatusEnum, int> ByStatus { get; set; } = new Dictionary<IncidentStatusEnum, int>();

        /// <summary>
        /// Count of open incidents per severity.
        /// </summary>
        public Dictionary<IncidentSeverityEnum, int> OpenBySeverity { get; set; } = new Dictionary<IncidentSeverityEnum, int>();

        #endregion
    }
}
