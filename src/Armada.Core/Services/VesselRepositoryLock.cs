namespace Armada.Core.Services
{
    using System;
    using System.Collections.Concurrent;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Per-vessel serialization slot for every operation that moves a vessel's landing refs.
    /// </summary>
    /// <remarks>
    /// Mission landing, merge-queue entry processing and operator branch writes all read a target
    /// tip, compute a new commit and then advance the target. Two of those interleaved on one
    /// vessel can publish a stale tip or silently drop the other's commit, so they share this one
    /// slot keyed by vessel id. The slot is process-wide because the landing handler, the merge
    /// queue and the branch write routes are constructed separately but act on the same
    /// repositories. It is not re-entrant: a holder must never wait for the same vessel again.
    /// </remarks>
    public static class VesselRepositoryLock
    {
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> _Slots = new ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.Ordinal);

        /// <summary>
        /// Wait for the vessel's slot.
        /// </summary>
        /// <param name="vesselId">Vessel identifier; a missing id shares one fallback slot.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>A lease; dispose it to release the slot.</returns>
        public static async Task<IDisposable> AcquireAsync(string? vesselId, CancellationToken token = default)
        {
            SemaphoreSlim slot = GetSlot(vesselId);
            await slot.WaitAsync(token).ConfigureAwait(false);
            return new Lease(slot);
        }

        /// <summary>
        /// Take the vessel's slot only when it is free right now.
        /// </summary>
        /// <param name="vesselId">Vessel identifier; a missing id shares one fallback slot.</param>
        /// <returns>A lease, or null when another operation holds the slot.</returns>
        public static IDisposable? TryAcquire(string? vesselId)
        {
            SemaphoreSlim slot = GetSlot(vesselId);
            return slot.Wait(0) ? new Lease(slot) : null;
        }

        private static SemaphoreSlim GetSlot(string? vesselId)
        {
            string key = String.IsNullOrWhiteSpace(vesselId) ? "unknown" : vesselId;
            return _Slots.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        }

        private sealed class Lease : IDisposable
        {
            private readonly SemaphoreSlim _Slot;
            private int _Released = 0;

            public Lease(SemaphoreSlim slot)
            {
                _Slot = slot ?? throw new ArgumentNullException(nameof(slot));
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _Released, 1) == 0)
                {
                    _Slot.Release();
                }
            }
        }
    }
}
