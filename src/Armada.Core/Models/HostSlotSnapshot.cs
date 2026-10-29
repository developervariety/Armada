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
        /// How many of one vessel's suite commands may run at once. Defaults to 1.
        /// </summary>
        public int VesselSlotCount { get; set; } = 1;

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
        /// Available memory in megabytes the host must have before the next request is granted; 0 when the
        /// memory check is off.
        /// </summary>
        public int MemoryFloorMb { get; set; } = 0;

        /// <summary>
        /// The last available-memory reading in megabytes, or null when no reading was taken or it was unknown.
        /// </summary>
        public long? AvailableMemoryMb { get; set; } = null;

        /// <summary>
        /// When <see cref="AvailableMemoryMb"/> was read, or null when no reading was taken.
        /// </summary>
        public DateTime? AvailableMemoryReadUtc { get; set; } = null;

        /// <summary>
        /// True when a slot is free but the first request in line waits because available memory is below
        /// the floor.
        /// </summary>
        public bool HeldForMemory { get; set; } = false;

        /// <summary>
        /// When available memory first held the first request in line, or null when memory holds no request.
        /// </summary>
        public DateTime? MemoryHeldSinceUtc { get; set; } = null;

        /// <summary>
        /// Why the first request in line waits although a slot is free, such as
        /// "waiting for free memory: 4,100 MB available, floor 6,144 MB", or null when memory holds no request.
        /// </summary>
        public string? MemoryWaitReason { get; set; } = null;

        /// <summary>
        /// How many requests this process granted while available memory was below the floor, because they
        /// had waited the maximum memory wait with no slot held.
        /// </summary>
        public long UnderFloorGrants { get; set; } = 0;

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

        /// <summary>
        /// Requests waiting for a vessel suite slot, first in each vessel's line first.
        /// </summary>
        public List<HostVesselWaiter> VesselWaiters
        {
            get => _VesselWaiters;
            set => _VesselWaiters = value ?? new List<HostVesselWaiter>();
        }

        #endregion

        #region Private-Members

        private List<HostSlotHolder> _Holders = new List<HostSlotHolder>();
        private List<HostSlotWaiter> _Waiters = new List<HostSlotWaiter>();
        private List<HostVesselWaiter> _VesselWaiters = new List<HostVesselWaiter>();

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

        /// <summary>
        /// The vessel-suite waiter with the given key, or null when that key is not waiting on a vessel slot.
        /// </summary>
        /// <param name="key">Requester key.</param>
        /// <returns>The waiter, or null.</returns>
        public HostVesselWaiter? FindVesselWaiter(string? key)
        {
            if (String.IsNullOrEmpty(key)) return null;
            foreach (HostVesselWaiter waiter in _VesselWaiters)
            {
                if (String.Equals(waiter.Key, key, StringComparison.Ordinal)) return waiter;
            }

            return null;
        }

        #endregion
    }
}
