namespace Armada.Core.Models
{
    /// <summary>
    /// Captain coverage for one effective pipeline role.
    /// </summary>
    public class ObjectiveDispatchRole
    {
        /// <summary>
        /// Persona required by the effective pipeline.
        /// </summary>
        public string Persona { get; set; } = String.Empty;

        /// <summary>
        /// Effective model or tier preference for the role.
        /// </summary>
        public string? PreferredModel { get; set; } = null;

        /// <summary>
        /// Configured, usable captains that satisfy the role. Busy captains remain in this list.
        /// </summary>
        public List<string> EligibleConfiguredCaptainIds { get; set; } = new List<string>();

        /// <summary>
        /// Number of eligible captains that are currently idle. This is capacity information only.
        /// </summary>
        public int IdleEligibleCount { get; set; }
    }
}
