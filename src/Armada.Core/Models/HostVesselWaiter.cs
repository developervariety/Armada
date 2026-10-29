namespace Armada.Core.Models
{
    using System;

    /// <summary>
    /// One request waiting for a vessel's in-process suite slot, before it queues for a host command slot.
    /// </summary>
    public class HostVesselWaiter
    {
        #region Public-Members

        /// <summary>
        /// The vessel whose suite this request runs.
        /// </summary>
        public string VesselKey { get; set; } = String.Empty;

        /// <summary>
        /// Place in that vessel's line; 1 is granted next.
        /// </summary>
        public int Position { get; set; } = 0;

        /// <summary>
        /// Key of the requester, such as a mission ID, or null for an unnamed request.
        /// </summary>
        public string? Key { get; set; } = null;

        /// <summary>
        /// What the requester runs.
        /// </summary>
        public string Description { get; set; } = String.Empty;

        /// <summary>
        /// When the request joined the vessel queue.
        /// </summary>
        public DateTime RequestedUtc { get; set; } = DateTime.UtcNow;

        #endregion
    }
}
