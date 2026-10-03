namespace Armada.Core.Models
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// Point-in-time view of the host-wide build and test slot: who holds it and who waits, in grant order.
    /// </summary>
    public class HostSlotSnapshot
    {
        #region Public-Members

        /// <summary>
        /// Key of the current holder, such as a check run ID, or null when the slot is free or the holder is unnamed.
        /// </summary>
        public string? HolderKey { get; set; } = null;

        /// <summary>
        /// What the current holder runs, or null when the slot is free.
        /// </summary>
        public string? HolderDescription { get; set; } = null;

        /// <summary>
        /// When the current holder was granted the slot, or null when the slot is free.
        /// </summary>
        public DateTime? HolderSinceUtc { get; set; } = null;

        /// <summary>
        /// Requests waiting for the slot, first in line first.
        /// </summary>
        public List<HostSlotWaiter> Waiters
        {
            get => _Waiters;
            set => _Waiters = value ?? new List<HostSlotWaiter>();
        }

        #endregion

        #region Private-Members

        private List<HostSlotWaiter> _Waiters = new List<HostSlotWaiter>();

        #endregion

        #region Public-Methods

        /// <summary>
        /// The waiting request with the given key, or null when that key is not waiting.
        /// </summary>
        /// <param name="key">Requester key.</param>
        /// <returns>The waiter, or null.</returns>
        public HostSlotWaiter? FindWaiter(string? key)
        {
            if (String.IsNullOrEmpty(key)) return null;
            foreach (HostSlotWaiter waiter in _Waiters)
            {
                if (String.Equals(waiter.Key, key, StringComparison.Ordinal)) return waiter;
            }

            return null;
        }

        #endregion
    }
}
