namespace Armada.Core.Models
{
    /// <summary>
    /// Outcome of one operator probe of a captain's configured model. The probe sends no mission.
    /// </summary>
    public sealed class CaptainModelProbeResult
    {
        /// <summary>
        /// Captain that was probed.
        /// </summary>
        public string CaptainId { get; set; } = "";

        /// <summary>
        /// True when the configured model accepted the probe.
        /// </summary>
        public bool Success { get; set; } = false;

        /// <summary>
        /// Time the probe took, in milliseconds.
        /// </summary>
        public long LatencyMs { get; set; } = 0;

        /// <summary>
        /// Short failure reason. Null when <see cref="Success"/> is true.
        /// </summary>
        public string? Error { get; set; } = null;
    }
}
