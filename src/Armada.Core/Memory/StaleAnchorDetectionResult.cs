namespace Armada.Core.Memory
{
    using System.Collections.Generic;

    /// <summary>Result of a stale anchor detection scan for a vessel's accepted reflection memory.</summary>
    public sealed class StaleAnchorDetectionResult
    {
        #region Public-Members

        /// <summary>Vessel that was scanned.</summary>
        public string VesselId { get; set; } = "";

        /// <summary>Number of reflection.accepted events inspected.</summary>
        public int CheckedEventCount { get; set; }

        /// <summary>Whether file-existence checks were performed (requires vessel.LocalPath to be set).</summary>
        public bool FileChecksAvailable { get; set; }

        /// <summary>Reason file checks were skipped; null when file checks ran or no anchors had file paths.</summary>
        public string? SkipReason { get; set; }

        /// <summary>Staleness warnings discovered during the scan. Empty when all anchors are current.</summary>
        public List<StaleAnchorWarning> Warnings { get; set; } = new List<StaleAnchorWarning>();

        #endregion
    }
}
