namespace Armada.Core.Settings
{
    /// <summary>
    /// Settings controlling the autonomous objective scheduler.
    /// </summary>
    public sealed class AutonomousObjectiveSchedulerSettings
    {
        /// <summary>
        /// Whether the autonomous objective scheduler is enabled. Defaults to false (opt-in).
        /// </summary>
        public bool Enabled { get; set; } = false;

        /// <summary>
        /// Interval in minutes between scheduler polling cycles. Clamped to [1, 1440].
        /// </summary>
        public int IntervalMinutes
        {
            get => _IntervalMinutes;
            set => _IntervalMinutes = Math.Max(1, Math.Min(1440, value));
        }

        /// <summary>
        /// Maximum number of autonomous voyages the scheduler may run concurrently. Clamped to [1, 100].
        /// </summary>
        public int MaxConcurrentVoyages
        {
            get => _MaxConcurrentVoyages;
            set => _MaxConcurrentVoyages = Math.Max(1, Math.Min(100, value));
        }

        /// <summary>
        /// When true, the scheduler is paused and will not dispatch new voyages until resumed.
        /// </summary>
        public bool Paused { get; set; } = false;

        private int _IntervalMinutes = 25;
        private int _MaxConcurrentVoyages = 1;
    }
}
