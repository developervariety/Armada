namespace Armada.Core.Models
{
    using System.Collections.Generic;

    /// <summary>Safe checkout candidates visible to a global administrator.</summary>
    public sealed class VesselCheckoutDiscovery
    {
        /// <summary>Whether at least one checkout root is configured.</summary>
        public bool Configured { get; set; }

        /// <summary>Configured roots after path normalization where possible.</summary>
        public List<string> Roots { get; set; } = new List<string>();

        /// <summary>Valid direct-child working-tree repositories.</summary>
        public List<VesselCheckoutCandidate> Candidates { get; set; } = new List<VesselCheckoutCandidate>();

        /// <summary>Bounded, non-sensitive root and scan errors.</summary>
        public List<string> Errors { get; set; } = new List<string>();

        /// <summary>Whether more valid candidates existed after the result limit.</summary>
        public bool Truncated { get; set; }
    }
}
