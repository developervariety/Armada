namespace Armada.Core.Services
{
    using System.Security.Cryptography;
    using System.Text;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Settings;
    using SyslogLogging;

    /// <summary>
    /// Serializes every new work unit across server processes and enforces the configured fleet
    /// and transitive sibling-lane limits against fresh durable state.
    /// </summary>
    public sealed class FleetCapacityAdmission
    {
        private readonly DatabaseDriver _Database;
        private readonly ArmadaSettings _Settings;
        private readonly LoggingModule? _Logging;
        private readonly TimeSpan _LeaseTtl;

        /// <summary>
        /// Instantiate.
        /// </summary>
        public FleetCapacityAdmission(
            DatabaseDriver database,
            ArmadaSettings settings,
            LoggingModule? logging = null,
            TimeSpan? leaseTtl = null)
        {
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _Logging = logging;
            _LeaseTtl = leaseTtl ?? TimeSpan.FromMinutes(2);
            if (_LeaseTtl <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(leaseTtl));
        }

        /// <summary>
        /// Reserve capacity for a new voyage or for a mission added to an existing or bare voyage.
        /// Existing work in the same voyage consumes no second global slot. A newly touched sibling
        /// lane still requires lane capacity.
        /// </summary>
        public async Task<FleetCapacityReservation> AcquireAsync(
            Vessel vessel,
            string? candidateVoyageId,
            CancellationToken token = default)
        {
            if (vessel == null) throw new ArgumentNullException(nameof(vessel));
            string tenantId = vessel.TenantId ?? String.Empty;
            string leaseName = BuildLeaseName(tenantId);
            string holder = "fleet-capacity:" + Guid.NewGuid().ToString("N");
            bool acquired = false;

            try
            {
                while (!acquired)
                {
                    token.ThrowIfCancellationRequested();
                    acquired = await _Database.CoordinationLeases.TryAcquireAsync(
                        leaseName,
                        holder,
                        _LeaseTtl,
                        tenantId,
                        token).ConfigureAwait(false);
                    if (!acquired) await Task.Delay(25, token).ConfigureAwait(false);
                }

                CapacitySnapshot snapshot = await ReadSnapshotAsync(tenantId, token).ConfigureAwait(false);
                bool existingWorkUnit = !String.IsNullOrWhiteSpace(candidateVoyageId)
                    && snapshot.VesselsByWorkUnit.ContainsKey(candidateVoyageId!);
                string[] candidateLane = snapshot.Lanes.MembersFor(vessel.Id)
                    .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                HashSet<string> candidateLaneSet = candidateLane.ToHashSet(StringComparer.OrdinalIgnoreCase);
                int globalLimit = _Settings.AutonomousObjectiveScheduler.MaxConcurrentVoyages;
                if (!existingWorkUnit && snapshot.VesselsByWorkUnit.Count >= globalLimit)
                {
                    throw new FleetCapacityAdmissionException(
                        "fleet_capacity_reached",
                        "Fleet capacity is full: " + snapshot.VesselsByWorkUnit.Count
                            + " active work unit(s), limit " + globalLimit + ".",
                        snapshot.VesselsByWorkUnit.Count,
                        globalLimit,
                        vessel.Id,
                        candidateLane);
                }

                bool voyageAlreadyTouchesLane = existingWorkUnit
                    && snapshot.VesselsByWorkUnit[candidateVoyageId!].Any(candidateLaneSet.Contains);
                int activeInLane = snapshot.VesselsByWorkUnit.Count(pair => pair.Value.Any(candidateLaneSet.Contains));
                int laneLimit = _Settings.AutonomousObjectiveScheduler.MaxConcurrentVoyagesPerVessel;
                if (!voyageAlreadyTouchesLane && activeInLane >= laneLimit)
                {
                    throw new FleetCapacityAdmissionException(
                        "sibling_lane_capacity_reached",
                        "Sibling lane capacity is full for vessel " + vessel.Id + ": " + activeInLane
                            + " active work unit(s), limit " + laneLimit + ".",
                        activeInLane,
                        laneLimit,
                        vessel.Id,
                        candidateLane);
                }

                return new FleetCapacityReservation(
                    _Database.CoordinationLeases,
                    leaseName,
                    holder,
                    _LeaseTtl,
                    _Logging);
            }
            catch
            {
                if (acquired)
                {
                    try
                    {
                        await _Database.CoordinationLeases.ReleaseAsync(
                            leaseName,
                            holder,
                            CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _Logging?.Warn("[FleetCapacityAdmission] could not release failed admission "
                            + leaseName + ": " + ex.Message);
                    }
                }
                throw;
            }
        }

        /// <summary>
        /// Build the tenant-scoped lease name shared by all work-creation paths.
        /// </summary>
        public static string BuildLeaseName(string? tenantId)
        {
            byte[] input = Encoding.UTF8.GetBytes((tenantId ?? String.Empty).Trim());
            return "fleet-capacity:" + Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
        }

        private async Task<CapacitySnapshot> ReadSnapshotAsync(string tenantId, CancellationToken token)
        {
            List<Vessel> vessels = String.IsNullOrEmpty(tenantId)
                ? await _Database.Vessels.EnumerateAsync(token).ConfigureAwait(false)
                : await _Database.Vessels.EnumerateAsync(tenantId, token).ConfigureAwait(false);
            VesselLaneMap lanes = VesselLaneMap.Build(vessels,
                message => _Logging?.Warn("[FleetCapacityAdmission] " + message));

            List<Voyage> voyages = String.IsNullOrEmpty(tenantId)
                ? await _Database.Voyages.EnumerateAsync(token).ConfigureAwait(false)
                : await _Database.Voyages.EnumerateAsync(tenantId, token).ConfigureAwait(false);
            HashSet<string> activeVoyageIds = voyages
                .Where(voyage => voyage.Status == VoyageStatusEnum.Open || voyage.Status == VoyageStatusEnum.InProgress)
                .Select(voyage => voyage.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            List<Mission> missions = String.IsNullOrEmpty(tenantId)
                ? await _Database.Missions.EnumerateAsync(token).ConfigureAwait(false)
                : await _Database.Missions.EnumerateAsync(tenantId, token).ConfigureAwait(false);
            Dictionary<string, HashSet<string>> vesselsByWorkUnit =
                new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (Mission mission in missions)
            {
                if (String.IsNullOrWhiteSpace(mission.VesselId)) continue;
                string workUnitId;
                if (!String.IsNullOrWhiteSpace(mission.VoyageId))
                {
                    if (!activeVoyageIds.Contains(mission.VoyageId!)) continue;
                    workUnitId = mission.VoyageId!;
                }
                else
                {
                    if (!IsActiveMission(mission.Status)) continue;
                    workUnitId = "mission:" + mission.Id;
                }

                if (!vesselsByWorkUnit.TryGetValue(workUnitId, out HashSet<string>? touched))
                {
                    touched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    vesselsByWorkUnit[workUnitId] = touched;
                }
                touched.Add(mission.VesselId!);
            }

            return new CapacitySnapshot(lanes, vesselsByWorkUnit);
        }

        private static bool IsActiveMission(MissionStatusEnum status)
        {
            return status == MissionStatusEnum.Pending
                || status == MissionStatusEnum.Assigned
                || status == MissionStatusEnum.InProgress
                || status == MissionStatusEnum.WorkProduced
                || status == MissionStatusEnum.PullRequestOpen
                || status == MissionStatusEnum.Testing
                || status == MissionStatusEnum.Review;
        }

        private sealed class CapacitySnapshot
        {
            public CapacitySnapshot(VesselLaneMap lanes, Dictionary<string, HashSet<string>> vesselsByWorkUnit)
            {
                Lanes = lanes;
                VesselsByWorkUnit = vesselsByWorkUnit;
            }

            public VesselLaneMap Lanes { get; }
            public Dictionary<string, HashSet<string>> VesselsByWorkUnit { get; }
        }
    }
}
