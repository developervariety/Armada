namespace Armada.Core.Database.Interfaces
{
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// Database operations for captains.
    /// </summary>
    public interface ICaptainMethods
    {
        /// <summary>
        /// Create a captain.
        /// </summary>
        Task<Captain> CreateAsync(Captain captain, CancellationToken token = default);

        /// <summary>
        /// Read a captain by identifier.
        /// </summary>
        Task<Captain?> ReadAsync(string id, CancellationToken token = default);

        /// <summary>
        /// Read a captain by name.
        /// </summary>
        Task<Captain?> ReadByNameAsync(string name, CancellationToken token = default);

        /// <summary>
        /// Update a captain.
        /// </summary>
        Task<Captain> UpdateAsync(Captain captain, CancellationToken token = default);

        /// <summary>
        /// Delete a captain by identifier.
        /// </summary>
        Task DeleteAsync(string id, CancellationToken token = default);

        /// <summary>
        /// Enumerate all captains.
        /// </summary>
        Task<List<Captain>> EnumerateAsync(CancellationToken token = default);

        /// <summary>
        /// Enumerate captains with pagination and filtering.
        /// </summary>
        Task<EnumerationResult<Captain>> EnumerateAsync(EnumerationQuery query, CancellationToken token = default);

        /// <summary>
        /// Enumerate captains by state.
        /// </summary>
        Task<List<Captain>> EnumerateByStateAsync(CaptainStateEnum state, CancellationToken token = default);

        /// <summary>
        /// Update captain state.
        /// </summary>
        Task UpdateStateAsync(string id, CaptainStateEnum state, CancellationToken token = default);

        /// <summary>
        /// Update captain heartbeat timestamp.
        /// </summary>
        Task UpdateHeartbeatAsync(string id, CancellationToken token = default);

        /// <summary>
        /// Check if a captain exists by identifier.
        /// </summary>
        Task<bool> ExistsAsync(string id, CancellationToken token = default);

        /// <summary>
        /// Atomically claim a captain for a mission. Sets state to Working and assigns
        /// mission/dock IDs, but only if the captain is currently Idle.
        /// Returns true if the claim succeeded, false if the captain was no longer Idle.
        /// </summary>
        Task<bool> TryClaimAsync(string captainId, string missionId, string dockId, CancellationToken token = default);

        /// <summary>
        /// Read a captain by tenant and identifier (tenant-scoped).
        /// </summary>
        Task<Captain?> ReadAsync(string tenantId, string id, CancellationToken token = default);

        /// <summary>
        /// Delete a captain by tenant and identifier (tenant-scoped).
        /// </summary>
        Task DeleteAsync(string tenantId, string id, CancellationToken token = default);

        /// <summary>
        /// Enumerate all captains in a tenant (tenant-scoped).
        /// </summary>
        Task<List<Captain>> EnumerateAsync(string tenantId, CancellationToken token = default);

        /// <summary>
        /// Enumerate captains with pagination and filtering (tenant-scoped).
        /// </summary>
        Task<EnumerationResult<Captain>> EnumerateAsync(string tenantId, EnumerationQuery query, CancellationToken token = default);
    }
}
