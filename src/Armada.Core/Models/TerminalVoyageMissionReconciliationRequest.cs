namespace Armada.Core.Models
{
    using System;

    /// <summary>
    /// Parameters for one terminal-voyage mission reconciliation pass.
    /// </summary>
    public sealed class TerminalVoyageMissionReconciliationRequest
    {
        #region Public-Members

        /// <summary>
        /// Report what would change without writing anything. Defaults to true.
        /// </summary>
        public bool DryRun { get; set; } = true;

        /// <summary>
        /// Include voyages that ended before <see cref="Lookback"/>. The automatic health-loop pass
        /// leaves this false so only recently ended voyages are reconciled; the operator repair sets it.
        /// </summary>
        public bool IncludeHistorical { get; set; } = false;

        /// <summary>
        /// Restrict the pass to one voyage.
        /// </summary>
        public string? VoyageId { get; set; } = null;

        /// <summary>
        /// Restrict the pass to one vessel.
        /// </summary>
        public string? VesselId { get; set; } = null;

        /// <summary>
        /// Minimum time since the voyage ended before its missions are reconciled, so a landing that
        /// is still finishing after the voyage turned terminal is not overtaken. Clamped to zero or more.
        /// </summary>
        public TimeSpan Grace
        {
            get => _Grace;
            set => _Grace = value < TimeSpan.Zero ? TimeSpan.Zero : value;
        }

        /// <summary>
        /// Oldest voyage end the pass considers when <see cref="IncludeHistorical"/> is false. Clamped to at least the grace.
        /// </summary>
        public TimeSpan Lookback
        {
            get => _Lookback < _Grace ? _Grace : _Lookback;
            set => _Lookback = value < TimeSpan.Zero ? TimeSpan.Zero : value;
        }

        /// <summary>
        /// Maximum per-mission items returned in the result. Counts always cover every mission. Clamped to 0..5000.
        /// </summary>
        public int MaxItems
        {
            get => _MaxItems;
            set => _MaxItems = Math.Max(0, Math.Min(5000, value));
        }

        /// <summary>
        /// Clock override for tests. Null uses the current UTC time.
        /// </summary>
        public DateTime? NowUtc { get; set; } = null;

        #endregion

        #region Private-Members

        private TimeSpan _Grace = TimeSpan.FromMinutes(10);
        private TimeSpan _Lookback = TimeSpan.FromHours(24);
        private int _MaxItems = 200;

        #endregion
    }
}
