namespace Armada.Core.Services
{
    using System.Collections.Concurrent;
    using System.Security.Cryptography;
    using System.Text;
    using Armada.Core.Database;
    using Armada.Core.Models;
    using SyslogLogging;

    /// <summary>
    /// Provides a durable, multi-instance-safe reservation for the short mission-assignment
    /// transition across all build-participating sibling vessels. Assignment passes of one
    /// server run in parallel, so passes in this process queue for the lane, up to a bounded
    /// wait, instead of failing: a pass that holds the lane only to find no captain must not
    /// defer every other mission of the lane on every tick. A reservation held by another
    /// server process still fails at once.
    /// </summary>
    public sealed class SiblingLaneAdmission
    {
        private static readonly TimeSpan _LeaseTtl = TimeSpan.FromMinutes(15);
        private static readonly TimeSpan _DefaultQueueWait = TimeSpan.FromSeconds(30);
        private readonly DatabaseDriver _Database;
        private readonly LoggingModule _Logging;
        private readonly TimeSpan _QueueWait;
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _LaneGates =
            new ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.Ordinal);

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="logging">Logging module.</param>
        /// <param name="queueWait">Longest time a pass in this process waits for another pass of this process to release the lane; null uses 30 seconds.</param>
        public SiblingLaneAdmission(DatabaseDriver database, LoggingModule logging, TimeSpan? queueWait = null)
        {
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
            _QueueWait = queueWait ?? _DefaultQueueWait;
        }

        /// <summary>
        /// Resolve and reserve every member of the candidate vessel's sibling lane.
        /// </summary>
        public async Task<SiblingLaneReservation?> TryReserveAsync(Vessel vessel, string missionId, CancellationToken token = default)
        {
            if (vessel == null) throw new ArgumentNullException(nameof(vessel));
            if (String.IsNullOrWhiteSpace(missionId)) throw new ArgumentNullException(nameof(missionId));

            List<Vessel> vessels = String.IsNullOrWhiteSpace(vessel.TenantId)
                ? await _Database.Vessels.EnumerateAsync(token).ConfigureAwait(false)
                : await _Database.Vessels.EnumerateAsync(vessel.TenantId, token).ConfigureAwait(false);
            VesselLaneMap map = VesselLaneMap.Build(vessels,
                message => _Logging.Warn("[SiblingLaneAdmission] " + message));
            string[] members = map.MembersFor(vessel.Id)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray();
            string laneKey = (vessel.TenantId ?? String.Empty) + "\n" + String.Join("\n", members);
            SemaphoreSlim gate = _LaneGates.GetOrAdd(laneKey, _ => new SemaphoreSlim(1, 1));
            if (!await gate.WaitAsync(_QueueWait, token).ConfigureAwait(false)) return null;

            string holder = "mission-assignment:" + missionId + ":" + Guid.NewGuid().ToString("N");
            List<string> acquired = new List<string>();

            try
            {
                foreach (string member in members)
                {
                    string leaseName = BuildLeaseName(vessel.TenantId, member);
                    bool won = await _Database.CoordinationLeases.TryAcquireAsync(
                        leaseName, holder, _LeaseTtl, vessel.TenantId, token).ConfigureAwait(false);
                    if (!won) return null;
                    acquired.Add(leaseName);
                }

                return new SiblingLaneReservation(_Database, holder, members, acquired, gate);
            }
            finally
            {
                if (acquired.Count != members.Length)
                {
                    foreach (string leaseName in acquired)
                    {
                        await _Database.CoordinationLeases.ReleaseAsync(leaseName, holder, CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                    gate.Release();
                }
            }
        }

        private static string BuildLeaseName(string? tenantId, string vesselId)
        {
            byte[] input = Encoding.UTF8.GetBytes((tenantId ?? String.Empty) + "\n" + vesselId);
            return "mission-lane-vessel:" + Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
        }
    }

    /// <summary>
    /// A held set of sibling-lane coordination leases.
    /// </summary>
    public sealed class SiblingLaneReservation : IAsyncDisposable
    {
        private readonly DatabaseDriver _Database;
        private readonly string _Holder;
        private readonly IReadOnlyList<string> _LeaseNames;
        private readonly SemaphoreSlim? _Gate;
        private int _Released;

        internal SiblingLaneReservation(
            DatabaseDriver database,
            string holder,
            IReadOnlyList<string> members,
            IReadOnlyList<string> leaseNames,
            SemaphoreSlim? gate = null)
        {
            _Database = database;
            _Holder = holder;
            Members = members;
            _LeaseNames = leaseNames;
            _Gate = gate;
        }

        /// <summary>
        /// Vessel identifiers in the reserved lane.
        /// </summary>
        public IReadOnlyList<string> Members { get; }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _Released, 1) != 0) return;
            try
            {
                foreach (string leaseName in _LeaseNames)
                {
                    await _Database.CoordinationLeases.ReleaseAsync(leaseName, _Holder, CancellationToken.None)
                        .ConfigureAwait(false);
                }
            }
            finally
            {
                _Gate?.Release();
            }
        }
    }
}
