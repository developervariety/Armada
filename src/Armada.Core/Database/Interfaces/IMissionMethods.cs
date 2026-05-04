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
