namespace Armada.Core.Models
{
    using System.Collections.Generic;

    /// <summary>
    /// Request to clean up selected resources from a vessel cleanup inventory.
    /// </summary>
    public class VesselCleanupRequest
    {
        /// <summary>
        /// Keys of the resources to clean up (from <see cref="VesselCleanupResource.Key"/>).
        /// </summary>
        public List<string> Keys { get; set; } = new List<string>();
    }
}
