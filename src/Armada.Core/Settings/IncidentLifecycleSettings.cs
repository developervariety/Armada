namespace Armada.Core.Settings
{
    /// <summary>
    /// Settings for autonomous incident lifecycle transitions.
    /// </summary>
    public class IncidentLifecycleSettings
    {
        /// <summary>
        /// Whether Armada should evaluate open incidents during heartbeat maintenance.
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Whether objective evidence may move incidents from Open to Mitigated or RolledBack.
        /// </summary>
        public bool AutoMitigate { get; set; } = true;

        /// <summary>
        /// Whether mitigated incidents may be closed after the quiet period.
        /// </summary>
        public bool AutoClose { get; set; } = true;

        /// <summary>
        /// Minutes a mitigated incident must remain quiet before Armada closes it.
        /// </summary>
        public int CloseQuietPeriodMinutes
        {
            get => _CloseQuietPeriodMinutes;
            set => _CloseQuietPeriodMinutes = Math.Max(0, Math.Min(10080, value));
        }

        /// <summary>
        /// Maximum incidents evaluated per sweep.
        /// </summary>
        public int MaxIncidentsPerSweep
        {
            get => _MaxIncidentsPerSweep;
            set => _MaxIncidentsPerSweep = Math.Max(1, Math.Min(500, value));
        }

        private int _CloseQuietPeriodMinutes = 60;
        private int _MaxIncidentsPerSweep = 50;
    }
}
