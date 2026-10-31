namespace Armada.Core.Models
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;

    /// <summary>
    /// How long one granted request waited for its host-wide build and test slot, why, and which holders were
    /// ahead of it when it asked.
    /// </summary>
    public class HostSlotWaitRecord
    {
        #region Public-Members

        /// <summary>
        /// Milliseconds the request waited for its vessel's suite slot before it queued for a host slot. 0 when the
        /// request named no vessel or the vessel slot was free.
        /// </summary>
        public long VesselWaitMs { get; set; } = 0;

        /// <summary>
        /// Milliseconds from the request joining the host queue to its grant.
        /// </summary>
        public long HostWaitMs { get; set; } = 0;

        /// <summary>
        /// Milliseconds from the first time the request, first in line, was held because available memory was below
        /// the floor, to its grant. 0 when memory never held it.
        /// </summary>
        public long MemoryHeldMs { get; set; } = 0;

        /// <summary>
        /// Milliseconds from the first time the request, first in line, was held by a request that holds the host
        /// alone (or because it asked for the host alone while a slot was held), to its grant. 0 when that never
        /// held it.
        /// </summary>
        public long AloneHeldMs { get; set; } = 0;

        /// <summary>
        /// Requests ahead of this one in the host queue when it joined.
        /// </summary>
        public int AheadAtRequest { get; set; } = 0;

        /// <summary>
        /// The holders of this vessel's suite slots and of the host slots when the request had to wait for them, as
        /// "key: description". Empty when the request waited for no holder.
        /// </summary>
        public List<string> BlockedBy { get; set; } = new List<string>();

        #endregion

        #region Public-Methods

        /// <summary>
        /// One line an operator or a census can read: every wait in milliseconds and the blocking holders.
        /// </summary>
        /// <returns>The line, without a trailing newline.</returns>
        public string Describe()
        {
            return "vessel_wait_ms=" + VesselWaitMs.ToString(CultureInfo.InvariantCulture)
                + "; host_wait_ms=" + HostWaitMs.ToString(CultureInfo.InvariantCulture)
                + "; memory_held_ms=" + MemoryHeldMs.ToString(CultureInfo.InvariantCulture)
                + "; alone_held_ms=" + AloneHeldMs.ToString(CultureInfo.InvariantCulture)
                + "; ahead_at_request=" + AheadAtRequest.ToString(CultureInfo.InvariantCulture)
                + "; blocked_by=" + (BlockedBy.Count == 0 ? "none" : String.Join(", ", BlockedBy));
        }

        #endregion
    }
}
