namespace Armada.Core.Services
{
    using System;
    using System.Collections.Concurrent;
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

        private static readonly ConcurrentDictionary<string, byte> _Waiting =
            new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);

        private static readonly ConcurrentDictionary<string, Lazy<Task<IDisposable>>> _InFlight =
            new ConcurrentDictionary<string, Lazy<Task<IDisposable>>>(StringComparer.Ordinal);

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
            return _Waiting.ContainsKey(missionId) || _Held.ContainsKey(missionId);
        }

        /// <summary>
        /// True when the mission is waiting for a suite lease and does not yet hold one.
        /// </summary>
        /// <param name="missionId">Mission identifier.</param>
        /// <returns>True when the mission is queued.</returns>
        public static bool IsWaiting(string? missionId)
        {
            if (String.IsNullOrWhiteSpace(missionId)) return false;
            return _Waiting.ContainsKey(missionId);
        }

        /// <summary>
        /// Acquire the suite lease for a mission. The request takes the vessel suite slot and then a host
        /// command slot. A second acquire for the same mission returns the held lease.
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
                _InFlight.TryRemove(id, out Lazy<Task<IDisposable>> _);
            }
        }

        private static async Task<IDisposable> AcquireCoreAsync(string missionId, string? vesselKey, CancellationToken token)
        {
            _Waiting[missionId] = 0;
            try
            {
                IDisposable lease = await HostWideCommandLock.AcquireAsync(
                    missionId,
                    "captain suite lease for mission " + missionId,
                    vesselKey,
                    token).ConfigureAwait(false);
                _Held[missionId] = lease;
                return lease;
            }
            finally
            {
                _Waiting.TryRemove(missionId, out byte _);
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
            if (!_Held.TryRemove(missionId.Trim(), out IDisposable? lease) || lease == null) return false;
            lease.Dispose();
            return true;
        }

        /// <summary>
        /// Release a lease the mission still holds when the mission ends. Waiting acquires are not cancelled
        /// here; the tool call's cancellation token ends those.
        /// </summary>
        /// <param name="missionId">Mission identifier.</param>
        public static void ReleaseForMission(string? missionId)
        {
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
