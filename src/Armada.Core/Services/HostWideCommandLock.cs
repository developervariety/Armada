namespace Armada.Core.Services
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;

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
    /// </remarks>
    public static class HostWideCommandLock
    {
        private static readonly SemaphoreSlim _Slot = new SemaphoreSlim(1, 1);

        /// <summary>
        /// Acquire the host-wide execution slot.
        /// </summary>
        /// <param name="token">Cancellation token.</param>
        /// <returns>A disposable lease; dispose it to release the slot.</returns>
        public static async Task<IDisposable> AcquireAsync(CancellationToken token = default)
        {
            await _Slot.WaitAsync(token).ConfigureAwait(false);
            return new Lease(_Slot);
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
