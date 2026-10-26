namespace Armada.Core.Models
{
    using System.Collections.Generic;

    /// <summary>Input for starting a run against an ordered selection of vessels.</summary>
    public sealed class FleetActionRunRequest
    {
        /// <summary>Stable caller request identifier used for replay detection.</summary>
        public string RequestId { get; set; } = string.Empty;
        /// <summary>Selected vessel identifiers.</summary>
        public List<string> VesselIds { get; set; } = new List<string>();
    }
}
