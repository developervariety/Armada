namespace Armada.Core.Settings
{
    /// <summary>
    /// Settings for detecting near-instant runtime exit-code-1 crash loops and benching captains.
    /// </summary>
    public sealed class CrashLoopDetectionSettings
    {
        /// <summary>
        /// Whether crash-loop detection is active.
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Consecutive near-instant exit-1 failures required before benching.
        /// </summary>
        public int FailureThreshold
        {
            get => _FailureThreshold;
            set => _FailureThreshold = Math.Max(2, value);
        }

        /// <summary>
        /// Maximum runtime in seconds for a launch to count as near-instant.
        /// </summary>
        public int MaxRuntimeSeconds
        {
            get => _MaxRuntimeSeconds;
            set => _MaxRuntimeSeconds = Math.Max(1, Math.Min(60, value));
        }

        /// <summary>
        /// Seconds a benched captain remains ineligible before restore sweep may clear the bench.
        /// </summary>
        public int CooldownSeconds
        {
            get => _CooldownSeconds;
            set => _CooldownSeconds = Math.Max(30, Math.Min(3600, value));
        }

        /// <summary>
        /// Whether restore should run a probe launch before clearing the bench.
        /// </summary>
        public bool UseProbeOnRestore { get; set; } = false;

        private int _FailureThreshold = 3;
        private int _MaxRuntimeSeconds = 5;
        private int _CooldownSeconds = 300;
    }
}
