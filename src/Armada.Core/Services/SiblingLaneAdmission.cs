namespace Armada.Core.Services
{
    using System.Security.Cryptography;
    using System.Text;
    using Armada.Core.Database;
    using Armada.Core.Models;
    using SyslogLogging;

    /// <summary>
    /// Provides a durable, multi-instance-safe reservation for the short mission-assignment
    /// transition across all build-participating sibling vessels.
    /// </summary>
    public sealed class SiblingLaneAdmission
    {
        private static readonly TimeSpan _LeaseTtl = TimeSpan.FromMinutes(15);
        private readonly DatabaseDriver _Database;
        private readonly LoggingModule _Logging;

        /// <summary>
        /// Instantiate.
        /// </summary>
        public SiblingLaneAdmission(DatabaseDriver database, LoggingModule logging)
        {
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
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

                return new SiblingLaneReservation(_Database, holder, members, acquired);
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
        private int _Released;

        internal SiblingLaneReservation(
            DatabaseDriver database,
            string holder,
            IReadOnlyList<string> members,
            IReadOnlyList<string> leaseNames)
        {
            _Database = database;
            _Holder = holder;
            Members = members;
            _LeaseNames = leaseNames;
        }

        /// <summary>
        /// Vessel identifiers in the reserved lane.
        /// </summary>
        public IReadOnlyList<string> Members { get; }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _Released, 1) != 0) return;
            foreach (string leaseName in _LeaseNames)
            {
                await _Database.CoordinationLeases.ReleaseAsync(leaseName, _Holder, CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
    }
}
