namespace Armada.Core.Settings
{
    /// <summary>
    /// Settings for auto-quarantining captains that hit provider usage or quota limits.
    /// </summary>
    public sealed class CaptainQuarantineSettings
    {
        /// <summary>
        /// Default seconds to keep a captain quarantined when the provider does not publish a reset time.
        /// </summary>
        public int DefaultBackoffSeconds
        {
            get => _DefaultBackoffSeconds;
            set => _DefaultBackoffSeconds = System.Math.Max(30, System.Math.Min(3600, value));
        }

        /// <summary>
        /// When true, expired quarantine restore runs a lightweight probe before returning the captain to Idle.
        /// </summary>
        public bool UseProbeOnRestore { get; set; } = false;

        private int _DefaultBackoffSeconds = 300;
    }
}
