namespace Armada.Core.Models
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// Point-in-time view of the host-wide build and test slots: who holds them and who waits, in grant order.
    /// </summary>
    public class HostSlotSnapshot
    {
        #region Public-Members

        /// <summary>
        /// How many requests may hold a slot at once. Defaults to 1.
        /// </summary>
        public int SlotCount { get; set; } = 1;

        /// <summary>
        /// Key of the earliest current holder, such as a check run ID, or null when every slot is free or that
        /// holder is unnamed. <see cref="Holders"/> lists every holder.
        /// </summary>
        public string? HolderKey { get; set; } = null;

        /// <summary>
        /// What the earliest current holder runs, or null when every slot is free.
        /// </summary>
        public string? HolderDescription { get; set; } = null;

        /// <summary>
        /// When the earliest current holder was granted its slot, or null when every slot is free.
        /// </summary>
        public DateTime? HolderSinceUtc { get; set; } = null;

        /// <summary>
        /// Every request that holds a slot now, earliest grant first.
        /// </summary>
        public List<HostSlotHolder> Holders
        {
            get => _Holders;
            set => _Holders = value ?? new List<HostSlotHolder>();
        }

        /// <summary>
        /// Requests waiting for a slot, first in line first.
        /// </summary>
        public List<HostSlotWaiter> Waiters
        {
            get => _Waiters;
            set => _Waiters = value ?? new List<HostSlotWaiter>();
        }

        #endregion

        #region Private-Members

        private List<HostSlotHolder> _Holders = new List<HostSlotHolder>();
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

        /// <summary>
        /// The holder with the given key, or null when that key holds no slot.
        /// </summary>
        /// <param name="key">Requester key.</param>
        /// <returns>The holder, or null.</returns>
        public HostSlotHolder? FindHolder(string? key)
        {
            if (String.IsNullOrEmpty(key)) return null;
            foreach (HostSlotHolder holder in _Holders)
            {
                if (String.Equals(holder.Key, key, StringComparison.Ordinal)) return holder;
            }

            return null;
        }

        #endregion
    }
}
