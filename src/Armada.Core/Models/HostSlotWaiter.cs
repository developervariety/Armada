namespace Armada.Core.Models
{
    using System;

    /// <summary>
    /// One request waiting for the host-wide build and test slot.
    /// </summary>
    public class HostSlotWaiter
    {
        #region Public-Members

        /// <summary>
        /// Place in line; 1 is granted next.
        /// </summary>
        public int Position { get; set; } = 0;

        /// <summary>
        /// Key of the requester, such as a check run ID, or null for an unnamed request.
        /// </summary>
        public string? Key { get; set; } = null;

        /// <summary>
        /// What the requester runs.
        /// </summary>
        public string Description { get; set; } = String.Empty;

        /// <summary>
        /// When the request joined the queue.
        /// </summary>
        public DateTime RequestedUtc { get; set; } = DateTime.UtcNow;

        #endregion
    }
}
