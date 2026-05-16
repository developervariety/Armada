namespace Armada.Core.Models
{
    /// <summary>
    /// One detected or required toolchain probe result for vessel readiness.
    /// </summary>
    public class VesselToolchainProbe
    {
        /// <summary>
        /// Toolchain or executable name.
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Whether the repository contents suggest this toolchain is relevant.
        /// </summary>
        public bool Expected { get; set; } = false;

        /// <summary>
        /// Whether the toolchain executable appears available on the host.
        /// </summary>
        public bool Available { get; set; } = false;

        /// <summary>
        /// Best-effort version string when available.
        /// </summary>
        public string? Version { get; set; } = null;

        /// <summary>
        /// Repository file or heuristic that caused the probe.
        /// </summary>
        public string? Evidence { get; set; } = null;
    }
}
