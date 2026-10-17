namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;

    /// <summary>
    /// Host-wide execution slots for expensive build and test commands.
    /// </summary>
    /// <remarks>
    /// The DoD gate, check runs, and merge-queue test runs all execute a vessel's full build and
    /// unit-test command. More of those at once than the host can carry produce failures that look
    /// like broken code but are not, and each contended run stretches wall time beyond the
    /// quiet-window baseline. The resource they contend for is the machine's CPU and disk, not any
    /// single vessel, so the interlock is host-wide: every caller that runs an expensive command must
    /// acquire a slot here. The host grants at most <see cref="SlotCount"/> slots at once, one by
    /// default, so by default it runs one full suite at a time. Callers must hold a dock lease or
    /// other liveness guarantee across the wait, because a request can sit behind other callers'
    /// full build and test runs.
    ///
    /// Slots are granted strictly in request order, and a caller holds one for one command, not for a
    /// sequence of them, so a check that asked first is never passed over by a later phase of another
    /// vessel's gate. Raising the slot count grants waiting requests at once, in order; lowering it
    /// takes no slot from a holder and grants nothing new until the holders drop below the new count.
    /// Every request names its owner, so <see cref="Snapshot"/> can say who holds each slot and where
    /// each waiter stands.
    /// </remarks>
    public static class HostWideCommandLock
    {
        #region Public-Members

        /// <summary>
        /// Fewest slots the host grants at once.
        /// </summary>
        public const int MinimumSlots = 1;

        /// <summary>
        /// Most slots the host grants at once.
        /// </summary>
        public const int MaximumSlots = 8;

        /// <summary>
        /// How many requests may hold a slot at once. Defaults to 1.
        /// </summary>
        public static int SlotCount
        {
            get
            {
                lock (_Sync)
                {
                    return _SlotCount;
                }
            }
        }

        #endregion

        #region Private-Members

        private static readonly object _Sync = new object();
        private static readonly LinkedList<Request> _Waiters = new LinkedList<Request>();
        private static readonly List<Request> _Holders = new List<Request>();
        private static int _SlotCount = MinimumSlots;

        #endregion

        #region Public-Methods

        /// <summary>
        /// Clamp a slot count to [<see cref="MinimumSlots"/>, <see cref="MaximumSlots"/>].
        /// </summary>
        /// <param name="slots">Requested slot count.</param>
        /// <returns>The clamped count.</returns>
        public static int ClampSlotCount(int slots)
        {
            return Math.Max(MinimumSlots, Math.Min(MaximumSlots, slots));
        }

        /// <summary>
        /// Set how many requests may hold a slot at once. The count is clamped to
        /// [<see cref="MinimumSlots"/>, <see cref="MaximumSlots"/>]. A raised count grants waiting
        /// requests at once, in request order; a lowered count takes no slot from a current holder.
        /// </summary>
        /// <param name="slots">Requested slot count.</param>
        public static void SetSlotCount(int slots)
        {
            List<Request> granted = new List<Request>();
            lock (_Sync)
            {
                _SlotCount = ClampSlotCount(slots);
                GrantWaiting(granted);
            }

            Complete(granted);
        }

        /// <summary>
        /// Acquire a host-wide execution slot for an unnamed caller.
        /// </summary>
        /// <param name="token">Cancellation token.</param>
        /// <returns>A disposable lease; dispose it to release the slot.</returns>
        public static Task<IDisposable> AcquireAsync(CancellationToken token = default)
        {
            return AcquireAsync(null, null, token);
        }

        /// <summary>
        /// Acquire a host-wide execution slot. Requests are granted in the order they were made.
        /// </summary>
        /// <param name="ownerKey">Stable key of the requester, such as a check run ID, used to find its place in the queue.</param>
        /// <param name="ownerDescription">What the requester runs, shown to anyone waiting behind it.</param>
        /// <param name="token">Cancellation token. Cancelling a waiting request removes it from the queue.</param>
        /// <returns>A disposable lease; dispose it to release the slot.</returns>
        public static async Task<IDisposable> AcquireAsync(string? ownerKey, string? ownerDescription, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            Request request = new Request(ownerKey, ownerDescription);
            LinkedListNode<Request>? node = null;

            lock (_Sync)
            {
                if (_Holders.Count < _SlotCount && _Waiters.Count == 0)
                {
                    request.GrantedUtc = DateTime.UtcNow;
                    _Holders.Add(request);
                    return new Lease(request);
                }

                node = _Waiters.AddLast(request);
            }

            using (token.Register(() => CancelWaiting(node)))
            {
                await request.Granted.Task.ConfigureAwait(false);
            }

            return new Lease(request);
        }

        /// <summary>
        /// Who holds the slots and who waits for one, in grant order.
        /// </summary>
        /// <returns>A point-in-time view of the slots.</returns>
        public static HostSlotSnapshot Snapshot()
        {
            lock (_Sync)
            {
                HostSlotSnapshot snapshot = new HostSlotSnapshot();
                snapshot.SlotCount = _SlotCount;
                foreach (Request holder in _Holders)
                {
                    snapshot.Holders.Add(new HostSlotHolder
                    {
                        Key = holder.OwnerKey,
                        Description = holder.OwnerDescription,
                        SinceUtc = holder.GrantedUtc
                    });
                }

                if (_Holders.Count > 0)
                {
                    Request earliest = _Holders[0];
                    snapshot.HolderKey = earliest.OwnerKey;
                    snapshot.HolderDescription = earliest.OwnerDescription;
                    snapshot.HolderSinceUtc = earliest.GrantedUtc;
                }

                int position = 1;
                foreach (Request waiter in _Waiters)
                {
                    snapshot.Waiters.Add(new HostSlotWaiter
                    {
                        Position = position++,
                        Key = waiter.OwnerKey,
                        Description = waiter.OwnerDescription,
                        RequestedUtc = waiter.RequestedUtc
                    });
                }

                return snapshot;
            }
        }

        #endregion

        #region Private-Methods

        private static void CancelWaiting(LinkedListNode<Request>? node)
        {
            if (node == null) return;
            lock (_Sync)
            {
                // A request granted just before cancellation keeps its slot; its caller releases it.
                if (node.List == null) return;
                _Waiters.Remove(node);
            }

            node.Value.Granted.TrySetCanceled();
        }

        private static void Release(Request request)
        {
            List<Request> granted = new List<Request>();
            lock (_Sync)
            {
                if (!_Holders.Remove(request)) return;
                GrantWaiting(granted);
            }

            Complete(granted);
        }

        /// <summary>
        /// Move waiters to holders, first in line first, while a slot is free. Call under <see cref="_Sync"/>;
        /// complete the returned requests outside it.
        /// </summary>
        private static void GrantWaiting(List<Request> granted)
        {
            while (_Holders.Count < _SlotCount && _Waiters.First != null)
            {
                Request next = _Waiters.First.Value;
                _Waiters.RemoveFirst();
                next.GrantedUtc = DateTime.UtcNow;
                _Holders.Add(next);
                granted.Add(next);
            }
        }

        private static void Complete(List<Request> granted)
        {
            foreach (Request request in granted)
            {
                request.Granted.TrySetResult(true);
            }
        }

        #endregion

        #region Private-Classes

        private sealed class Request
        {
            public Request(string? ownerKey, string? ownerDescription)
            {
                OwnerKey = String.IsNullOrWhiteSpace(ownerKey) ? null : ownerKey.Trim();
                OwnerDescription = String.IsNullOrWhiteSpace(ownerDescription) ? "an unnamed build or test command" : ownerDescription.Trim();
                RequestedUtc = DateTime.UtcNow;
            }

            public string? OwnerKey { get; }

            public string OwnerDescription { get; }

            public DateTime RequestedUtc { get; }

            public DateTime? GrantedUtc { get; set; }

            public TaskCompletionSource<bool> Granted { get; } = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private sealed class Lease : IDisposable
        {
            private readonly Request _Request;
            private int _Released = 0;

            public Lease(Request request)
            {
                _Request = request ?? throw new ArgumentNullException(nameof(request));
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _Released, 1) == 0)
                {
                    Release(_Request);
                }
            }
        }

        #endregion
    }
}
