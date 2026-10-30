namespace Armada.Core.Services
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;

    /// <summary>
    /// Per-mission suite leases captains take before a full-suite run. The lease is the same host command
    /// slot and per-vessel suite slot the definition-of-done gate and armed checks use, so a captain check
    /// counts against those limits. A lease is released when the captain releases it, when the mission ends,
    /// or when the acquire is cancelled.
    /// </summary>
    public static class CaptainSuiteLeaseRegistry
    {
        #region Public-Members

        /// <summary>Event written when a captain starts waiting for a suite lease.</summary>
        public const string WaitEventType = "captain.suite_lease_wait";

        #endregion

        #region Private-Members

        private static readonly ConcurrentDictionary<string, IDisposable> _Held =
            new ConcurrentDictionary<string, IDisposable>(StringComparer.Ordinal);

        private static readonly ConcurrentDictionary<string, Lazy<Task<IDisposable>>> _InFlight =
            new ConcurrentDictionary<string, Lazy<Task<IDisposable>>>(StringComparer.Ordinal);

        private static readonly ConcurrentDictionary<string, CancellationTokenSource> _WaitCancels =
            new ConcurrentDictionary<string, CancellationTokenSource>(StringComparer.Ordinal);

        // Serializes a grant against a withdrawal, so a lease granted while its wait is being cancelled is
        // either recorded before the release reads it or returned to the host lock unrecorded.
        private static readonly object _GrantLock = new object();

        #endregion

        #region Public-Methods

        /// <summary>
        /// True when the mission is waiting for or holding a captain suite lease.
        /// </summary>
        /// <param name="missionId">Mission identifier.</param>
        /// <returns>True when the lease is active.</returns>
        public static bool IsActive(string? missionId)
        {
            if (String.IsNullOrWhiteSpace(missionId)) return false;
            return _WaitCancels.ContainsKey(missionId) || _Held.ContainsKey(missionId);
        }

        /// <summary>
        /// True when the mission is waiting for a suite lease and does not yet hold one.
        /// </summary>
        /// <param name="missionId">Mission identifier.</param>
        /// <returns>True when the mission is queued.</returns>
        public static bool IsWaiting(string? missionId)
        {
            if (String.IsNullOrWhiteSpace(missionId)) return false;
            return _WaitCancels.ContainsKey(missionId);
        }

        /// <summary>
        /// Acquire the suite lease for a mission. The request takes the vessel suite slot and then a host
        /// command slot. A second acquire for the same mission returns the held lease, or joins the wait
        /// already queued for it and so keeps that wait's place.
        /// </summary>
        /// <param name="missionId">Mission that holds the lease.</param>
        /// <param name="vesselKey">Vessel whose suite the captain will run.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The granted lease. Dispose it, or call <see cref="Release"/>, to free the slots.</returns>
        public static async Task<IDisposable> AcquireAsync(string missionId, string? vesselKey, CancellationToken token)
        {
            if (String.IsNullOrWhiteSpace(missionId)) throw new ArgumentException("A mission id is required.", nameof(missionId));
            string id = missionId.Trim();
            if (_Held.TryGetValue(id, out IDisposable? existing) && existing != null) return existing;

            // Two acquires for one mission share one wait: a second call must not take another vessel slot
            // behind the first, which would deadlock the mission against itself.
            Lazy<Task<IDisposable>> inflight = _InFlight.GetOrAdd(id, key =>
                new Lazy<Task<IDisposable>>(
                    () => AcquireCoreAsync(key, vesselKey, token),
                    LazyThreadSafetyMode.ExecutionAndPublication));
            try
            {
                return await inflight.Value.ConfigureAwait(false);
            }
            finally
            {
                _InFlight.TryRemove(new KeyValuePair<string, Lazy<Task<IDisposable>>>(id, inflight));
            }
        }

        private static async Task<IDisposable> AcquireCoreAsync(string missionId, string? vesselKey, CancellationToken token)
        {
            using (CancellationTokenSource wait = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                _WaitCancels[missionId] = wait;
                try
                {
                    IDisposable lease = await HostWideCommandLock.AcquireAsync(
                        missionId,
                        "captain suite lease for mission " + missionId,
                        vesselKey,
                        wait.Token).ConfigureAwait(false);
                    lock (_GrantLock)
                    {
                        if (wait.IsCancellationRequested)
                        {
                            lease.Dispose();
                            wait.Token.ThrowIfCancellationRequested();
                        }
                        _Held[missionId] = lease;
                    }
                    return lease;
                }
                finally
                {
                    _WaitCancels.TryRemove(new KeyValuePair<string, CancellationTokenSource>(missionId, wait));
                }
            }
        }

        /// <summary>
        /// Release the suite lease this mission holds, if any.
        /// </summary>
        /// <param name="missionId">Mission identifier.</param>
        /// <returns>True when a held lease was released.</returns>
        public static bool Release(string? missionId)
        {
            if (String.IsNullOrWhiteSpace(missionId)) return false;
            IDisposable? lease;
            lock (_GrantLock)
            {
                if (!_Held.TryRemove(missionId.Trim(), out lease) || lease == null) return false;
            }
            lease.Dispose();
            return true;
        }

        /// <summary>
        /// Cancel the mission's queued acquire, if any, so it gives up its place and is never granted. The
        /// mission stops reading as waiting at once, and a later acquire starts a new request.
        /// </summary>
        /// <param name="missionId">Mission identifier.</param>
        /// <returns>True when a queued acquire was cancelled.</returns>
        public static bool CancelWait(string? missionId)
        {
            if (String.IsNullOrWhiteSpace(missionId)) return false;
            string id = missionId.Trim();
            lock (_GrantLock)
            {
                if (!_WaitCancels.TryRemove(id, out CancellationTokenSource? wait) || wait == null) return false;
                _InFlight.TryRemove(id, out Lazy<Task<IDisposable>>? _);
                try { wait.Cancel(); }
                catch (ObjectDisposedException) { return false; }
            }
            return true;
        }

        /// <summary>
        /// End the mission's lease when its captain process exits or the mission ends: a waiting acquire is
        /// cancelled and a held lease is released. A process that has exited can no longer run or release a
        /// suite, so a lease it kept would hold the vessel slot its own definition-of-done gate needs.
        /// </summary>
        /// <param name="missionId">Mission identifier.</param>
        public static void ReleaseForMission(string? missionId)
        {
            if (String.IsNullOrWhiteSpace(missionId)) return;
            CancelWait(missionId);
            Release(missionId);
        }

        /// <summary>
        /// Wait position for a mission that is queued for a vessel or host slot, or 0 when it is not waiting.
        /// </summary>
        /// <param name="missionId">Mission identifier.</param>
        /// <returns>The position, or 0.</returns>
        public static int ReadWaitPosition(string? missionId)
        {
            if (String.IsNullOrWhiteSpace(missionId)) return 0;
            HostSlotSnapshot snapshot = HostWideCommandLock.Snapshot();
            HostVesselWaiter? vesselWaiter = snapshot.FindVesselWaiter(missionId);
            if (vesselWaiter != null) return vesselWaiter.Position;
            HostSlotWaiter? hostWaiter = snapshot.FindWaiter(missionId);
            return hostWaiter?.Position ?? 0;
        }

        #endregion
    }
}
