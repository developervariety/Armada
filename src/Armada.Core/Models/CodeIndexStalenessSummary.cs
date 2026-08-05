namespace Armada.Core.Models
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// Fleet-wide code-index staleness summary for the status surface: which vessels'
    /// indexes are behind their repository HEAD, so staleness is visible before a
    /// dispatch attempt rather than only as a dispatch rejection.
    /// </summary>
    public class CodeIndexStalenessSummary
    {
        #region Public-Members

        /// <summary>
        /// UTC timestamp of the summary scan.
        /// </summary>
        public DateTime ScannedUtc { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Number of vessels whose index is stale.
        /// </summary>
        public int StaleVesselCount { get; set; } = 0;

        /// <summary>
        /// The stale vessels, with indexed vs current commits.
        /// </summary>
        public List<CodeIndexStaleVessel> StaleVessels { get; set; } = new List<CodeIndexStaleVessel>();

        #endregion
    }
}
