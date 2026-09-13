namespace Armada.Core.Models
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// Response from GET /api/v1/missions/{id}/log.
    /// </summary>
    public class MissionLogResponse
    {
        /// <summary>
        /// Mission ID.
        /// </summary>
        public string? MissionId { get; set; }

        /// <summary>
        /// Log content.
        /// </summary>
        public string? Log { get; set; }

        /// <summary>
        /// Number of lines returned.
        /// </summary>
        public int Lines { get; set; }

        /// <summary>
        /// Total number of lines in the log file.
        /// </summary>
        public int TotalLines { get; set; }

        /// <summary>Typed readable entries when formatted=true; null for the legacy text view.</summary>
        public List<Armada.Core.Services.FormattedLogLine>? Entries { get; set; }

        /// <summary>True when the formatted page omitted entries at its output limit.</summary>
        public bool EntriesTruncated { get; set; }

        /// <summary>
        /// Error message if log retrieval failed.
        /// </summary>
        public string? Error { get; set; }

        /// <summary>
        /// Error description.
        /// </summary>
        public string? Description { get; set; }
    }
}
