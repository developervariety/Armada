namespace Armada.Core.Services
{
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// Reads bounded pages of current fleet state for authoritative client reconciliation.
    /// </summary>
    public sealed class FleetReconciliationSnapshotService
    {
        private const int PageSize = 500;
        private const int MaximumPagesPerQuery = 2000;
        private const int MaximumSnapshotItems = 100000;
        private readonly DatabaseDriver _Database;

        /// <summary>
        /// Create the snapshot service.
        /// </summary>
        /// <param name="database">Database driver used for read-only state queries.</param>
        public FleetReconciliationSnapshotService(DatabaseDriver database)
        {
            _Database = database ?? throw new ArgumentNullException(nameof(database));
        }

        /// <summary>
        /// Get active fleet state, or complete linked state for one voyage including a terminal voyage.
        /// </summary>
        public async Task<FleetReconciliationSnapshot> GetAsync(
            string? voyageId = null,
            CancellationToken token = default)
        {
            voyageId = String.IsNullOrWhiteSpace(voyageId) ? null : voyageId.Trim();

            List<Voyage> voyages;
            if (voyageId != null)
            {
                Voyage? voyage = await _Database.Voyages.ReadAsync(voyageId, token).ConfigureAwait(false);
                voyages = voyage == null ? new List<Voyage>() : new List<Voyage> { voyage };
            }
            else
            {
                List<Voyage> open = await ReadVoyagePagesAsync(VoyageStatusEnum.Open, token).ConfigureAwait(false);
                List<Voyage> inProgress = await ReadVoyagePagesAsync(VoyageStatusEnum.InProgress, token).ConfigureAwait(false);
                voyages = open
                    .Concat(inProgress)
                    .GroupBy(voyage => voyage.Id, StringComparer.Ordinal)
                    .Select(group => group.First())
                    .OrderBy(voyage => voyage.CreatedUtc)
                    .ThenBy(voyage => voyage.Id, StringComparer.Ordinal)
                    .ToList();
            }

            List<MissionSummary> missions = new List<MissionSummary>();
            foreach (Voyage voyage in voyages)
            {
                List<MissionSummary> voyageMissions = await ReadMissionPagesAsync(voyage.Id, token).ConfigureAwait(false);
                missions.AddRange(voyageMissions);
                EnsureSnapshotSize(voyages.Count, missions.Count, 0, 0);
            }

            if (voyageId == null)
            {
                foreach (MissionStatusEnum activeStatus in ActiveMissionStatuses)
                    missions.AddRange(await ReadMissionPagesAsync(activeStatus, token).ConfigureAwait(false));
            }
            missions = missions
                .GroupBy(mission => mission.Id, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToList();

            HashSet<string> voyageIds = voyages.Select(voyage => voyage.Id).ToHashSet(StringComparer.Ordinal);
            HashSet<string> missionIdsForChecks = missions.Select(mission => mission.Id).ToHashSet(StringComparer.Ordinal);
            DateTime? checkWindowStart = voyages.Select(voyage => (DateTime?)voyage.CreatedUtc)
                .Concat(missions.Select(mission => (DateTime?)mission.CreatedUtc))
                .Min();
            List<CheckRun> checks = (await ReadRelevantCheckPagesAsync(checkWindowStart, token).ConfigureAwait(false))
                .Where(check => (!String.IsNullOrWhiteSpace(check.VoyageId) && voyageIds.Contains(check.VoyageId))
                    || (!String.IsNullOrWhiteSpace(check.MissionId) && missionIdsForChecks.Contains(check.MissionId)))
                .GroupBy(check => check.Id, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToList();

            List<Captain> allCaptains = await ReadCaptainPagesAsync(token).ConfigureAwait(false);
            if (voyageId != null)
            {
                HashSet<string> missionIds = missions.Select(mission => mission.Id).ToHashSet(StringComparer.Ordinal);
                HashSet<string> captainIds = missions
                    .Where(mission => !String.IsNullOrWhiteSpace(mission.CaptainId))
                    .Select(mission => mission.CaptainId!)
                    .ToHashSet(StringComparer.Ordinal);
                allCaptains = allCaptains
                    .Where(captain => captainIds.Contains(captain.Id)
                        || (!String.IsNullOrWhiteSpace(captain.CurrentMissionId)
                            && missionIds.Contains(captain.CurrentMissionId)))
                    .ToList();
            }
            EnsureSnapshotSize(voyages.Count, missions.Count, checks.Count, allCaptains.Count);

            return new FleetReconciliationSnapshot
            {
                GeneratedUtc = DateTime.UtcNow,
                VoyageId = voyageId,
                Voyages = voyages.Select(ProjectVoyage).ToList(),
                Missions = missions
                    .OrderBy(mission => mission.CreatedUtc)
                    .ThenBy(mission => mission.Id, StringComparer.Ordinal)
                    .ToList(),
                Captains = allCaptains
                    .OrderBy(captain => captain.Name, StringComparer.Ordinal)
                    .ThenBy(captain => captain.Id, StringComparer.Ordinal)
                    .Select(ProjectCaptain)
                    .ToList(),
                CheckRuns = checks
                    .OrderBy(check => check.CreatedUtc)
                    .ThenBy(check => check.Id, StringComparer.Ordinal)
                    .Select(ProjectCheckRun)
                    .ToList()
            };
        }

        private async Task<List<Voyage>> ReadVoyagePagesAsync(VoyageStatusEnum status, CancellationToken token)
        {
            return await ReadPagesAsync(
                page => _Database.Voyages.EnumerateAsync(new EnumerationQuery
                {
                    Status = status.ToString(),
                    PageNumber = page,
                    PageSize = PageSize,
                    Order = EnumerationOrderEnum.CreatedAscending
                }, token),
                "voyages").ConfigureAwait(false);
        }

        private async Task<List<MissionSummary>> ReadMissionPagesAsync(string voyageId, CancellationToken token)
        {
            return await ReadPagesAsync(
                page => _Database.Missions.EnumerateMissionSummariesAsync(new EnumerationQuery
                {
                    VoyageId = voyageId,
                    PageNumber = page,
                    PageSize = PageSize,
                    Order = EnumerationOrderEnum.CreatedAscending
                }, token),
                "missions for voyage '" + voyageId + "'").ConfigureAwait(false);
        }

        private async Task<List<MissionSummary>> ReadMissionPagesAsync(MissionStatusEnum status, CancellationToken token)
        {
            return await ReadPagesAsync(
                page => _Database.Missions.EnumerateMissionSummariesAsync(new EnumerationQuery
                {
                    Status = status.ToString(),
                    PageNumber = page,
                    PageSize = PageSize,
                    Order = EnumerationOrderEnum.CreatedAscending
                }, token),
                status + " missions").ConfigureAwait(false);
        }

        private async Task<List<Captain>> ReadCaptainPagesAsync(CancellationToken token)
        {
            return await ReadPagesAsync(
                page => _Database.Captains.EnumerateAsync(new EnumerationQuery
                {
                    PageNumber = page,
                    PageSize = PageSize,
                    Order = EnumerationOrderEnum.CreatedAscending
                }, token),
                "captains").ConfigureAwait(false);
        }

        private async Task<List<CheckRun>> ReadRelevantCheckPagesAsync(DateTime? fromUtc, CancellationToken token)
        {
            if (!fromUtc.HasValue) return new List<CheckRun>();

            return await ReadPagesAsync(
                page => _Database.CheckRuns.EnumerateAsync(new CheckRunQuery
                {
                    FromUtc = fromUtc.Value.AddSeconds(-1),
                    PageNumber = page,
                    PageSize = PageSize
                }, token),
                "Check runs").ConfigureAwait(false);
        }

        private static readonly MissionStatusEnum[] ActiveMissionStatuses =
        {
            MissionStatusEnum.Pending,
            MissionStatusEnum.Assigned,
            MissionStatusEnum.InProgress,
            MissionStatusEnum.WorkProduced,
            MissionStatusEnum.PullRequestOpen,
            MissionStatusEnum.Testing,
            MissionStatusEnum.Review,
            MissionStatusEnum.LandingFailed,
            MissionStatusEnum.WaitingForInput
        };

        private static async Task<List<T>> ReadPagesAsync<T>(
            Func<int, Task<EnumerationResult<T>>> readPage,
            string label)
        {
            List<T> result = new List<T>();
            for (int pageNumber = 1; pageNumber <= MaximumPagesPerQuery; pageNumber++)
            {
                EnumerationResult<T> page = await readPage(pageNumber).ConfigureAwait(false);
                if (!page.Success) throw new InvalidOperationException("Could not read " + label + ".");
                result.AddRange(page.Objects);
                if (result.Count > MaximumSnapshotItems)
                    throw new InvalidOperationException("The reconciliation snapshot item limit was exceeded while reading " + label + ".");

                if (pageNumber >= page.TotalPages || page.Objects.Count == 0) return result;
            }

            throw new InvalidOperationException(
                "The reconciliation snapshot exceeded the " + MaximumPagesPerQuery + "-page limit for " + label + ".");
        }

        private static void EnsureSnapshotSize(int voyageCount, int missionCount, int checkCount, int captainCount)
        {
            long total = (long)voyageCount + missionCount + checkCount + captainCount;
            if (total > MaximumSnapshotItems)
                throw new InvalidOperationException(
                    "The reconciliation snapshot exceeded the " + MaximumSnapshotItems + "-item limit.");
        }

        private static FleetSnapshotVoyage ProjectVoyage(Voyage voyage)
        {
            return new FleetSnapshotVoyage
            {
                Id = voyage.Id,
                Title = voyage.Title,
                Status = voyage.Status,
                CreatedUtc = voyage.CreatedUtc,
                CompletedUtc = voyage.CompletedUtc,
                LastUpdateUtc = voyage.LastUpdateUtc
            };
        }

        private static FleetSnapshotCaptain ProjectCaptain(Captain captain)
        {
            return new FleetSnapshotCaptain
            {
                Id = captain.Id,
                Name = captain.Name,
                Runtime = captain.Runtime,
                State = captain.State,
                CurrentMissionId = captain.CurrentMissionId,
                CurrentDockId = captain.CurrentDockId,
                ProcessId = captain.ProcessId,
                RecoveryAttempts = captain.RecoveryAttempts,
                LastHeartbeatUtc = captain.LastHeartbeatUtc,
                LastProcessAliveUtc = captain.LastProcessAliveUtc
            };
        }

        private static FleetSnapshotCheckRun ProjectCheckRun(CheckRun check)
        {
            return new FleetSnapshotCheckRun
            {
                Id = check.Id,
                VoyageId = check.VoyageId,
                MissionId = check.MissionId,
                VesselId = check.VesselId,
                Label = check.Label,
                Type = check.Type,
                Source = check.Source,
                Status = check.Status,
                CommitHash = check.CommitHash,
                QueueDurationMs = check.QueueDurationMs,
                DurationMs = check.DurationMs,
                CreatedUtc = check.CreatedUtc,
                StartedUtc = check.StartedUtc,
                CompletedUtc = check.CompletedUtc,
                LastUpdateUtc = check.LastUpdateUtc
            };
        }
    }
}
