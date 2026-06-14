namespace Armada.Core.Settings
{
    /// <summary>
    /// Settings for Armada-native mission recovery automation.
    /// </summary>
    public class AutonomousRecoverySettings
    {
        /// <summary>
        /// Whether the server should create incident/runbook records and apply recovery policy.
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Whether recoverable terminal mission failures should create a linked rescue mission.
        /// </summary>
        public bool DispatchRescueMissions { get; set; } = true;

        /// <summary>
        /// Whether heartbeat maintenance should send Mail nudges to live captains before hard recovery.
        /// </summary>
        public bool SendStallMailNudges { get; set; } = true;

        /// <summary>
        /// Maximum number of autonomous rescue missions Armada may create for one failed mission.
        /// </summary>
        public int MaxMissionRecoveryAttempts
        {
            get => _MaxMissionRecoveryAttempts;
            set => _MaxMissionRecoveryAttempts = Math.Max(0, Math.Min(5, value));
        }

        /// <summary>
        /// Failed missions older than this many hours are ignored by heartbeat sweeps.
        /// </summary>
        public int FailedMissionLookbackHours
        {
            get => _FailedMissionLookbackHours;
            set => _FailedMissionLookbackHours = Math.Max(1, Math.Min(168, value));
        }

        /// <summary>
        /// Minimum minutes between repeated automatic Mail nudges for the same captain.
        /// </summary>
        public int StallMailNudgeCooldownMinutes
        {
            get => _StallMailNudgeCooldownMinutes;
            set => _StallMailNudgeCooldownMinutes = Math.Max(1, Math.Min(240, value));
        }

        /// <summary>
        /// Fraction of StallThresholdMinutes after which Armada may send a Mail nudge.
        /// </summary>
        public double StallMailNudgeThresholdRatio
        {
            get => _StallMailNudgeThresholdRatio;
            set => _StallMailNudgeThresholdRatio = Math.Max(0.1, Math.Min(0.95, value));
        }

        /// <summary>
        /// Whether the periodic sweep should drain judge-passed WorkProduced branches on idle voyages.
        /// </summary>
        public bool LandingDrainEnabled { get; set; } = true;

        /// <summary>
        /// Open or idle voyages with no progress for this many minutes may open a stuck-voyage incident.
        /// </summary>
        public int StuckOpenVoyageMinutes
        {
            get => _StuckOpenVoyageMinutes;
            set => _StuckOpenVoyageMinutes = Math.Max(5, Math.Min(1440, value));
        }

        /// <summary>
        /// Maximum number of open voyages processed per landing-drain sweep pass.
        /// </summary>
        public int LandingDrainMaxVoyagesPerSweep
        {
            get => _LandingDrainMaxVoyagesPerSweep;
            set => _LandingDrainMaxVoyagesPerSweep = Math.Max(1, Math.Min(50, value));
        }

        private int _MaxMissionRecoveryAttempts = 1;
        private int _FailedMissionLookbackHours = 24;
        private int _StallMailNudgeCooldownMinutes = 15;
        private double _StallMailNudgeThresholdRatio = 0.5;
        private int _StuckOpenVoyageMinutes = 30;
        private int _LandingDrainMaxVoyagesPerSweep = 10;
    }
}
