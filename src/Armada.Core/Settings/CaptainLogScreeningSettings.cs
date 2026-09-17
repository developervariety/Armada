namespace Armada.Core.Settings
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// Settings for the read-only captain-log screen. The screen reads a bounded tail of each
    /// in-progress mission's log on a cadence, runs the registered passes over it, and posts a
    /// voyage-tagged board note plus one event when a pass reports a finding. It never cancels,
    /// pauses, mails, re-dispatches, or steers a mission.
    /// </summary>
    public class CaptainLogScreeningSettings
    {
        #region Public-Members

        /// <summary>
        /// Whether the screen runs. Default false, so a fresh install performs no log reads until
        /// an operator opts in.
        /// </summary>
        public bool Enabled { get; set; } = false;

        /// <summary>
        /// Minimum seconds between two sweeps. The cadence host may call the screen more often;
        /// a call inside the interval returns without reading any log. Clamped to at least 30.
        /// </summary>
        public int IntervalSeconds
        {
            get => _IntervalSeconds;
            set => _IntervalSeconds = Math.Max(30, value);
        }

        /// <summary>
        /// Number of trailing log lines read per mission per sweep. The screen never reads more
        /// than this. Clamped to the range 20 to 2000.
        /// </summary>
        public int TailLines
        {
            get => _TailLines;
            set => _TailLines = Math.Max(20, Math.Min(2000, value));
        }

        /// <summary>
        /// Minutes a mission stays in cooldown after a flag. While a mission is in cooldown the
        /// screen posts no further note for it. Clamped to at least 1.
        /// </summary>
        public int CooldownMinutes
        {
            get => _CooldownMinutes;
            set => _CooldownMinutes = Math.Max(1, value);
        }

        /// <summary>
        /// Boundary patterns for the boundary-token rule class. Operator configuration: the list is
        /// empty by default, so the rule reports nothing until an operator supplies the terms their
        /// deployment treats as private. Each entry is matched as a case-insensitive substring of a
        /// log line. Setting this to null restores the empty default list.
        /// </summary>
        public List<string> BoundaryPatterns
        {
            get => _BoundaryPatterns;
            set => _BoundaryPatterns = value ?? new List<string>();
        }

        #endregion

        #region Private-Members

        private int _IntervalSeconds = 300;
        private int _TailLines = 200;
        private int _CooldownMinutes = 30;
        private List<string> _BoundaryPatterns = new List<string>();

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate with the screen off and no boundary patterns.
        /// </summary>
        public CaptainLogScreeningSettings()
        {
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Copy every value from another instance into this one, in place, so a service that holds
        /// this instance by reference observes a settings reload. A sub-section left out of the
        /// hot-reload copy is silently ignored on every reload, so every member belongs here.
        /// </summary>
        /// <param name="source">Instance to copy values from. Null is ignored.</param>
        public void CopyFrom(CaptainLogScreeningSettings source)
        {
            if (source == null || ReferenceEquals(source, this)) return;
            Enabled = source.Enabled;
            IntervalSeconds = source.IntervalSeconds;
            TailLines = source.TailLines;
            CooldownMinutes = source.CooldownMinutes;
            BoundaryPatterns = new List<string>(source.BoundaryPatterns);
        }

        #endregion
    }
}
