namespace Armada.Core.Database.Interfaces
{
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// Database operations for missions.
    /// </summary>
    public interface IMissionMethods
    {
        /// <summary>
        /// Read status counts across all visible voyage missions and a page of distinct vessel IDs.
        /// A null tenant selects global scope; a user scope requires a tenant. Callers must authorize
        /// the voyage and scope before using this database operation. Page size is bounded to 100.
        /// </summary>
        Task<VoyageMissionSummary> ReadVoyageMissionSummaryAsync(string voyageId, int pageNumber = 1,
            int pageSize = 100, string? tenantId = null, string? userId = null, CancellationToken token = default)
        {
            throw new NotSupportedException("Voyage mission aggregates are not implemented by this database.");
        }

        /// <summary>
        /// Record admission evidence only if a loaded pending mission has not changed.
        /// A refused decision sets WaitingForResourcePressure in the same conditional write.
        /// This operation cannot assign work, restore ownership or change process identity.
        /// </summary>
        Task<bool> TryRecordAdmissionAsync(Mission expected, MissionAdmissionObservation observation,
            CancellationToken token = default)
        {
            throw new NotSupportedException("Admission observation persistence is not implemented by this database.");
        }

        /// <summary>
        /// Create a mission.
        /// </summary>
        Task<Mission> CreateAsync(Mission mission, CancellationToken token = default);

        /// <summary>
        /// Read a mission by identifier.
        /// </summary>
        Task<Mission?> ReadAsync(string id, CancellationToken token = default);

        /// <summary>
        /// Read a mission by identifier for status/list/detail surfaces that do not
        /// need large captured payloads. Implementations should avoid hydrating
        /// diff_snapshot, agent_output, and playbook snapshot content.
        /// </summary>
        async Task<Mission?> ReadSummaryAsync(string id, CancellationToken token = default)
        {
            Mission? mission = await ReadAsync(id, token).ConfigureAwait(false);
            if (mission != null) StripHeavyFields(new[] { mission });
            return mission;
        }

        /// <summary>
        /// Update a mission.
        /// </summary>
        Task<Mission> UpdateAsync(Mission mission, CancellationToken token = default);

        /// <summary>
        /// Update a mission only while its stored status still equals <paramref name="expectedStatus"/>. A writer that
        /// loaded the mission earlier uses this so it cannot overwrite a status another writer set in the meantime,
        /// such as a cancellation.
        /// </summary>
        /// <param name="mission">Mission to write.</param>
        /// <param name="expectedStatus">Status the stored row must still have.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>True when the row was written; false when the stored status had changed and nothing was written.</returns>
        Task<bool> TryUpdateIfStatusAsync(Mission mission, MissionStatusEnum expectedStatus, CancellationToken token = default);

        /// <summary>
        /// Update the mission heartbeat timestamp without rewriting the full record.
        /// Implementations should also advance the parent voyage LastUpdateUtc when applicable.
        /// </summary>
        Task UpdateHeartbeatAsync(string id, CancellationToken token = default);

        /// <summary>
        /// Delete a mission by identifier.
        /// </summary>
        Task DeleteAsync(string id, CancellationToken token = default);

        /// <summary>
        /// Enumerate all missions.
        /// </summary>
        Task<List<Mission>> EnumerateAsync(CancellationToken token = default);

        /// <summary>
        /// Enumerate missions with pagination and filtering.
        /// </summary>
        Task<EnumerationResult<Mission>> EnumerateAsync(EnumerationQuery query, CancellationToken token = default);

        /// <summary>
        /// Enumerate missions with pagination and filtering for list/dashboard surfaces.
        /// Implementations should avoid hydrating large mission text payloads such as
        /// description, diff_snapshot, agent_output, and playbook snapshot content.
        /// </summary>
        async Task<EnumerationResult<Mission>> EnumerateSummariesAsync(EnumerationQuery query, CancellationToken token = default)
        {
            EnumerationResult<Mission> result = await EnumerateAsync(query, token).ConfigureAwait(false);
            StripHeavyFields(result.Objects);
            return result;
        }

        /// <summary>
        /// Enumerate lightweight mission summary records with pagination and filtering.
        /// </summary>
        Task<EnumerationResult<MissionSummary>> EnumerateMissionSummariesAsync(EnumerationQuery query, CancellationToken token = default);

        /// <summary>
        /// Enumerate missions by voyage identifier.
        /// </summary>
        Task<List<Mission>> EnumerateByVoyageAsync(string voyageId, CancellationToken token = default);

        /// <summary>
        /// Enumerate lightweight mission summary records by voyage identifier.
        /// </summary>
        Task<List<MissionSummary>> EnumerateMissionSummariesByVoyageAsync(string voyageId, CancellationToken token = default);

        /// <summary>
        /// Enumerate missions by vessel identifier.
        /// </summary>
        Task<List<Mission>> EnumerateByVesselAsync(string vesselId, CancellationToken token = default);

        /// <summary>
        /// Enumerate lightweight mission summary records by vessel identifier.
        /// </summary>
        Task<List<MissionSummary>> EnumerateMissionSummariesByVesselAsync(string vesselId, CancellationToken token = default);

        /// <summary>
        /// Enumerate missions by captain identifier.
        /// </summary>
        Task<List<Mission>> EnumerateByCaptainAsync(string captainId, CancellationToken token = default);

        /// <summary>
        /// Enumerate lightweight mission summary records by captain identifier.
        /// </summary>
        Task<List<MissionSummary>> EnumerateMissionSummariesByCaptainAsync(string captainId, CancellationToken token = default);

        /// <summary>
        /// Enumerate missions by status.
        /// </summary>
        Task<List<Mission>> EnumerateByStatusAsync(MissionStatusEnum status, CancellationToken token = default);

        /// <summary>
        /// Count missions grouped by status without hydrating heavy text columns
        /// (description, diff_snapshot, agent_output). Used by status polling paths
        /// where the full mission rows are not needed.
        /// </summary>
        Task<Dictionary<MissionStatusEnum, int>> CountByStatusAsync(CancellationToken token = default);

        /// <summary>
        /// Enumerate lightweight mission history points in the supplied time range.
        /// </summary>
        Task<List<MissionHistoryPoint>> EnumerateHistoryPointsAsync(MissionHistoryQuery query, CancellationToken token = default);

        /// <summary>
        /// Count missions grouped by status for a specific tenant without hydrating rows.
        /// Backends should implement this with a GROUP BY query.
        /// Default falls back to enumerating all tenant missions in memory.
        /// </summary>
        async Task<Dictionary<MissionStatusEnum, int>> CountByStatusAsync(string tenantId, CancellationToken token = default)
        {
            List<Mission> all = await EnumerateAsync(tenantId, token).ConfigureAwait(false);
            return all
                .GroupBy(m => m.Status)
                .ToDictionary(g => g.Key, g => g.Count());
        }

        /// <summary>
        /// Get lightweight summaries (id, title, status) of active missions for a vessel.
        /// Only returns missions with status Assigned or InProgress.
        /// Implementations should avoid selecting heavy columns (description, diff_snapshot, agent_output).
        /// Default falls back to EnumerateByVesselAsync and filters in memory.
        /// </summary>
        async Task<List<ActiveMissionSummary>> GetActiveVesselSummariesAsync(string vesselId, CancellationToken token = default)
        {
            List<Mission> all = await EnumerateByVesselAsync(vesselId, token).ConfigureAwait(false);
            List<ActiveMissionSummary> summaries = new List<ActiveMissionSummary>();
            foreach (Mission m in all)
            {
                if (m.Status == MissionStatusEnum.Assigned || m.Status == MissionStatusEnum.InProgress)
                    summaries.Add(new ActiveMissionSummary { Id = m.Id, Title = m.Title ?? "", Status = m.Status });
            }
            return summaries;
        }

        /// <summary>
        /// Count missions in a voyage grouped by status without hydrating mission rows.
        /// </summary>
        async Task<Dictionary<MissionStatusEnum, int>> CountByVoyageStatusAsync(string voyageId, CancellationToken token = default)
        {
            List<Mission> missions = await EnumerateByVoyageAsync(voyageId, token).ConfigureAwait(false);
            return missions
                .GroupBy(m => m.Status)
                .ToDictionary(g => g.Key, g => g.Count());
        }

        /// <summary>
        /// Check if a mission exists by identifier.
        /// </summary>
        Task<bool> ExistsAsync(string id, CancellationToken token = default);

        /// <summary>
        /// Read a mission by tenant and identifier (tenant-scoped).
        /// </summary>
        Task<Mission?> ReadAsync(string tenantId, string id, CancellationToken token = default);

        /// <summary>
        /// Delete a mission by tenant and identifier (tenant-scoped).
        /// </summary>
        Task DeleteAsync(string tenantId, string id, CancellationToken token = default);

        /// <summary>
        /// Enumerate all missions in a tenant (tenant-scoped).
        /// </summary>
        Task<List<Mission>> EnumerateAsync(string tenantId, CancellationToken token = default);

        /// <summary>
        /// Enumerate missions with pagination and filtering (tenant-scoped).
        /// </summary>
        Task<EnumerationResult<Mission>> EnumerateAsync(string tenantId, EnumerationQuery query, CancellationToken token = default);

        /// <summary>
        /// Enumerate mission summaries with pagination and filtering (tenant-scoped).
        /// </summary>
        async Task<EnumerationResult<Mission>> EnumerateSummariesAsync(string tenantId, EnumerationQuery query, CancellationToken token = default)
        {
            EnumerationResult<Mission> result = await EnumerateAsync(tenantId, query, token).ConfigureAwait(false);
            StripHeavyFields(result.Objects);
            return result;
        }

        /// <summary>
        /// Enumerate lightweight mission summary records with pagination and filtering (tenant-scoped).
        /// </summary>
        Task<EnumerationResult<MissionSummary>> EnumerateMissionSummariesAsync(string tenantId, EnumerationQuery query, CancellationToken token = default);

        /// <summary>
        /// Enumerate missions by tenant and voyage identifier (tenant-scoped).
        /// </summary>
        Task<List<Mission>> EnumerateByVoyageAsync(string tenantId, string voyageId, CancellationToken token = default);

        /// <summary>
        /// Enumerate lightweight mission summary records by tenant and voyage identifier.
        /// </summary>
        Task<List<MissionSummary>> EnumerateMissionSummariesByVoyageAsync(string tenantId, string voyageId, CancellationToken token = default);

        /// <summary>
        /// Enumerate missions by tenant and vessel identifier (tenant-scoped).
        /// </summary>
        Task<List<Mission>> EnumerateByVesselAsync(string tenantId, string vesselId, CancellationToken token = default);

        /// <summary>
        /// Enumerate lightweight mission summary records by tenant and vessel identifier.
        /// </summary>
        Task<List<MissionSummary>> EnumerateMissionSummariesByVesselAsync(string tenantId, string vesselId, CancellationToken token = default);

        /// <summary>
        /// Enumerate missions by tenant and captain identifier (tenant-scoped).
        /// </summary>
        Task<List<Mission>> EnumerateByCaptainAsync(string tenantId, string captainId, CancellationToken token = default);

        /// <summary>
        /// Enumerate lightweight mission summary records by tenant and captain identifier.
        /// </summary>
        Task<List<MissionSummary>> EnumerateMissionSummariesByCaptainAsync(string tenantId, string captainId, CancellationToken token = default);

        /// <summary>
        /// Enumerate missions by tenant and status (tenant-scoped).
        /// </summary>
        Task<List<Mission>> EnumerateByStatusAsync(string tenantId, MissionStatusEnum status, CancellationToken token = default);

        /// <summary>
        /// Enumerate lightweight tenant-scoped mission history points in the supplied time range.
        /// </summary>
        Task<List<MissionHistoryPoint>> EnumerateHistoryPointsAsync(string tenantId, MissionHistoryQuery query, CancellationToken token = default);

        /// <summary>
        /// Check if a mission exists by tenant and identifier (tenant-scoped).
        /// </summary>
        Task<bool> ExistsAsync(string tenantId, string id, CancellationToken token = default);

        /// <summary>
        /// Read a mission by tenant, user, and identifier (user-scoped).
        /// </summary>
        Task<Mission?> ReadAsync(string tenantId, string userId, string id, CancellationToken token = default);

        /// <summary>
        /// Delete a mission by tenant, user, and identifier (user-scoped).
        /// </summary>
        Task DeleteAsync(string tenantId, string userId, string id, CancellationToken token = default);

        /// <summary>
        /// Enumerate all missions owned by a user within a tenant (user-scoped).
        /// </summary>
        Task<List<Mission>> EnumerateAsync(string tenantId, string userId, CancellationToken token = default);

        /// <summary>
        /// Enumerate missions with pagination and filtering (user-scoped).
        /// </summary>
        Task<EnumerationResult<Mission>> EnumerateAsync(string tenantId, string userId, EnumerationQuery query, CancellationToken token = default);

        /// <summary>
        /// Enumerate mission summaries with pagination and filtering (user-scoped).
        /// </summary>
        async Task<EnumerationResult<Mission>> EnumerateSummariesAsync(string tenantId, string userId, EnumerationQuery query, CancellationToken token = default)
        {
            EnumerationResult<Mission> result = await EnumerateAsync(tenantId, userId, query, token).ConfigureAwait(false);
            StripHeavyFields(result.Objects);
            return result;
        }

        /// <summary>
        /// Enumerate lightweight mission summary records with pagination and filtering (user-scoped).
        /// </summary>
        Task<EnumerationResult<MissionSummary>> EnumerateMissionSummariesAsync(string tenantId, string userId, EnumerationQuery query, CancellationToken token = default);

        /// <summary>
        /// Enumerate lightweight user-scoped mission history points in the supplied time range.
        /// </summary>
        Task<List<MissionHistoryPoint>> EnumerateHistoryPointsAsync(string tenantId, string userId, MissionHistoryQuery query, CancellationToken token = default);

        private static void StripHeavyFields(IEnumerable<Mission> missions)
        {
            foreach (Mission mission in missions)
            {
                mission.Description = null;
                mission.DiffSnapshot = null;
                mission.AgentOutput = null;
                mission.PlaybookSnapshots = new List<MissionPlaybookSnapshot>();
            }
        }
    }
}
