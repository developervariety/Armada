namespace Armada.Core.Settings
{
    /// <summary>
    /// Settings for detecting repeated runtime crash failures and quarantining captains.
    /// </summary>
    public sealed class CrashLoopDetectionSettings
    {
        /// <summary>
        /// Whether crash-loop detection is active.
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Distinct runtime crash failures required before quarantine. Values above 256 are capped because the
        /// tracker retains at most 256 failure identifiers per captain.
        /// </summary>
        public int FailureThreshold
        {
            get => _FailureThreshold;
            set => _FailureThreshold = Math.Max(2, Math.Min(256, value));
        }

        /// <summary>
        /// Seconds a crash-loop captain remains quarantined before restore may clear the hold.
        /// </summary>
        public int CooldownSeconds
        {
            get => _CooldownSeconds;
            set => _CooldownSeconds = Math.Max(30, Math.Min(3600, value));
        }

        /// <summary>
        /// Minutes in which non-provider crash failures count toward the crash-loop threshold.
        /// </summary>
        public int WindowMinutes
        {
            get => _WindowMinutes;
            set => _WindowMinutes = Math.Max(1, Math.Min(1440, value));
        }

        private int _FailureThreshold = 3;
        private int _CooldownSeconds = 300;
        private int _WindowMinutes = 10;
    }
}
