namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;

    /// <summary>
    /// Host-wide execution slot for expensive build and test commands.
    /// </summary>
    /// <remarks>
    /// The DoD gate, check runs, and merge-queue test runs all execute a vessel's full build and
    /// unit-test command. Two of those at once on one host produce failures that look like broken
    /// code but are not, and each contended run stretches wall time beyond the quiet-window
    /// baseline. The resource they contend for is the machine's CPU and disk, not any single
    /// vessel, so the interlock is host-wide: every caller that runs an expensive command must
    /// acquire this lock so the host runs at most one full suite at a time. Callers must hold a
    /// dock lease or other liveness guarantee across the wait, because the lock can sit behind
    /// another caller's full build and test run.
    ///
    /// The slot is granted strictly in request order, and a caller holds it for one command, not
    /// for a sequence of them, so a check that asked first is never passed over by a later phase
    /// of another vessel's gate. Every request names its owner, so <see cref="Snapshot"/> can say
    /// who holds the slot and where each waiter stands.
    /// </remarks>
    public static class HostWideCommandLock
    {
        #region Private-Members

        private static readonly object _Sync = new object();
        private static readonly LinkedList<Request> _Waiters = new LinkedList<Request>();
        private static Request? _Holder = null;

        #endregion

        #region Public-Methods

        /// <summary>
        /// Acquire the host-wide execution slot for an unnamed caller.
        /// </summary>
        /// <param name="token">Cancellation token.</param>
        /// <returns>A disposable lease; dispose it to release the slot.</returns>
        public static Task<IDisposable> AcquireAsync(CancellationToken token = default)
        {
            return AcquireAsync(null, null, token);
        }

        /// <summary>
        /// Acquire the host-wide execution slot. Requests are granted in the order they were made.
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
                if (_Holder == null && _Waiters.Count == 0)
                {
                    request.GrantedUtc = DateTime.UtcNow;
                    _Holder = request;
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
        /// Who holds the slot and who waits for it, in grant order.
        /// </summary>
        /// <returns>A point-in-time view of the slot.</returns>
        public static HostSlotSnapshot Snapshot()
        {
            lock (_Sync)
            {
                HostSlotSnapshot snapshot = new HostSlotSnapshot();
                if (_Holder != null)
                {
                    snapshot.HolderKey = _Holder.OwnerKey;
                    snapshot.HolderDescription = _Holder.OwnerDescription;
                    snapshot.HolderSinceUtc = _Holder.GrantedUtc;
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
            Request? next = null;
            lock (_Sync)
            {
                if (!ReferenceEquals(_Holder, request)) return;
                _Holder = null;
                if (_Waiters.First != null)
                {
                    next = _Waiters.First.Value;
                    _Waiters.RemoveFirst();
                    next.GrantedUtc = DateTime.UtcNow;
                    _Holder = next;
                }
            }

            next?.Granted.TrySetResult(true);
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
