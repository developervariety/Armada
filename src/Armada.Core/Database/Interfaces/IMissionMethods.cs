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
        /// Create a mission.
        /// </summary>
        Task<Mission> CreateAsync(Mission mission, CancellationToken token = default);

        /// <summary>
        /// Read a mission by identifier.
        /// </summary>
        Task<Mission?> ReadAsync(string id, CancellationToken token = default);

        /// <summary>
        /// Read a mission without returning heavy captured payloads such as diff snapshots,
        /// agent output, and playbook snapshots.
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
        /// Enumerate missions for list/dashboard surfaces without returning heavy text payloads.
        /// </summary>
        async Task<EnumerationResult<Mission>> EnumerateSummariesAsync(EnumerationQuery query, CancellationToken token = default)
        {
            EnumerationResult<Mission> result = await EnumerateAsync(query, token).ConfigureAwait(false);
            StripHeavyFields(result.Objects);
            return result;
        }

        /// <summary>
        /// Enumerate missions by voyage identifier.
        /// </summary>
        Task<List<Mission>> EnumerateByVoyageAsync(string voyageId, CancellationToken token = default);

        /// <summary>
        /// Enumerate missions by vessel identifier.
        /// </summary>
        Task<List<Mission>> EnumerateByVesselAsync(string vesselId, CancellationToken token = default);

        /// <summary>
        /// Enumerate missions by captain identifier.
        /// </summary>
        Task<List<Mission>> EnumerateByCaptainAsync(string captainId, CancellationToken token = default);

        /// <summary>
        /// Enumerate missions by status.
        /// </summary>
        Task<List<Mission>> EnumerateByStatusAsync(MissionStatusEnum status, CancellationToken token = default);

        /// <summary>
        /// Count missions grouped by status without hydrating heavy mission payload columns.
        /// </summary>
        Task<Dictionary<MissionStatusEnum, int>> CountByStatusAsync(CancellationToken token = default);

        /// <summary>
        /// Count missions grouped by status for a specific tenant without hydrating heavy mission payload columns.
        /// </summary>
        async Task<Dictionary<MissionStatusEnum, int>> CountByStatusAsync(string tenantId, CancellationToken token = default)
        {
            List<Mission> all = await EnumerateAsync(tenantId, token).ConfigureAwait(false);
            return all.GroupBy(m => m.Status).ToDictionary(g => g.Key, g => g.Count());
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
        /// Return lightweight summaries (id, title, status only) for active missions on a vessel.
        /// Avoids hydrating heavy columns (description, diff_snapshot, agent_output).
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
        /// Enumerate missions by tenant and voyage identifier (tenant-scoped).
        /// </summary>
        Task<List<Mission>> EnumerateByVoyageAsync(string tenantId, string voyageId, CancellationToken token = default);

        /// <summary>
        /// Enumerate missions by tenant and vessel identifier (tenant-scoped).
        /// </summary>
        Task<List<Mission>> EnumerateByVesselAsync(string tenantId, string vesselId, CancellationToken token = default);

        /// <summary>
        /// Enumerate missions by tenant and captain identifier (tenant-scoped).
        /// </summary>
        Task<List<Mission>> EnumerateByCaptainAsync(string tenantId, string captainId, CancellationToken token = default);

        /// <summary>
        /// Enumerate missions by tenant and status (tenant-scoped).
        /// </summary>
        Task<List<Mission>> EnumerateByStatusAsync(string tenantId, MissionStatusEnum status, CancellationToken token = default);

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
