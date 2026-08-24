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
        /// Maximum active objective voyages allowed on one vessel. Clamped to [1, 50].
        /// Defaults to one so fleet-wide concurrency cannot start conflicting suites on one vessel.
        /// </summary>
        public int MaxConcurrentVoyagesPerVessel
        {
            get => _MaxConcurrentVoyagesPerVessel;
            set => _MaxConcurrentVoyagesPerVessel = Math.Max(1, Math.Min(50, value));
        }

        /// <summary>
        /// When true, the scheduler is paused and will not dispatch new voyages until resumed.
        /// </summary>
        public bool Paused { get; set; } = false;

        /// <summary>
        /// Participant key of the session that set the pause, or null when the pause carries no
        /// attribution. A pause without an owner can only be cleared by an operator.
        /// </summary>
        public string? PausedBy { get; set; } = null;

        /// <summary>
        /// UTC time the pause was set, or null when unattributed.
        /// </summary>
        public DateTime? PausedUtc { get; set; } = null;

        /// <summary>
        /// Why the pause was set, as stated by the session that set it.
        /// </summary>
        public string? PauseReason { get; set; } = null;

        /// <summary>
        /// Minutes the pausing session must be absent from the coordination presence window
        /// before the autonomy layer may clear its pause. Owner decision: the floor is 30, twice
        /// the 15-minute presence default, because a deploy with verification finishes well inside
        /// that and a longer silence is not a deploy in progress. Clamped to 30-1440.
        /// </summary>
        public int StalePauseAbsenceMinutes
        {
            get => _StalePauseAbsenceMinutes;
            set => _StalePauseAbsenceMinutes = Math.Max(30, Math.Min(1440, value));
        }

        private int _StalePauseAbsenceMinutes = 30;
        private int _IntervalMinutes = 25;
        private int _MaxConcurrentVoyages = 1;
        private int _MaxConcurrentVoyagesPerVessel = 1;
    }
}
