namespace Armada.Core.Models
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// Counts and per-mission detail from one terminal-voyage mission reconciliation pass.
    /// </summary>
    public sealed class TerminalVoyageMissionReconciliationResult
    {
        #region Public-Members

        /// <summary>
        /// True when the pass wrote nothing.
        /// </summary>
        public bool DryRun { get; set; } = true;

        /// <summary>
        /// True when voyages that ended before the lookback were included.
        /// </summary>
        public bool IncludeHistorical { get; set; } = false;

        /// <summary>
        /// UTC time the pass evaluated against.
        /// </summary>
        public DateTime EvaluatedUtc { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// WorkProduced missions found under an ended voyage inside the pass window.
        /// </summary>
        public int Examined { get; set; } = 0;

        /// <summary>
        /// Missions moved (or, in a dry run, that would move) to Complete.
        /// </summary>
        public int Completed { get; set; } = 0;

        /// <summary>
        /// Missions moved (or that would move) to Failed.
        /// </summary>
        public int Failed { get; set; } = 0;

        /// <summary>
        /// Missions moved (or that would move) to Cancelled.
        /// </summary>
        public int Cancelled { get; set; } = 0;

        /// <summary>
        /// Missions left unchanged.
        /// </summary>
        public int Kept { get; set; } = 0;

        /// <summary>
        /// Count of decisions per reason code, covering every examined mission.
        /// </summary>
        public Dictionary<string, int> Reasons { get; set; } = new Dictionary<string, int>(StringComparer.Ordinal);

        /// <summary>
        /// Per-mission detail, bounded by the request's item limit.
        /// </summary>
        public List<TerminalVoyageMissionReconciliationItem> Items { get; set; } = new List<TerminalVoyageMissionReconciliationItem>();

        /// <summary>
        /// True when more missions were examined than <see cref="Items"/> holds.
        /// </summary>
        public bool ItemsTruncated { get; set; } = false;

        /// <summary>
        /// One-line summary of the pass.
        /// </summary>
        public string Summary { get; set; } = string.Empty;

        #endregion
    }
}
