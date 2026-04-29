namespace Armada.Core.Database.Interfaces
{
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// Database operations for planning sessions.
    /// </summary>
    public interface IPlanningSessionMethods
    {
        /// <summary>
        /// Create a planning session.
        /// </summary>
        Task<PlanningSession> CreateAsync(PlanningSession session, CancellationToken token = default);

        /// <summary>
        /// Read a planning session by identifier.
        /// </summary>
        Task<PlanningSession?> ReadAsync(string id, CancellationToken token = default);

        /// <summary>
        /// Update a planning session.
        /// </summary>
        Task<PlanningSession> UpdateAsync(PlanningSession session, CancellationToken token = default);

        /// <summary>
        /// Delete a planning session by identifier.
        /// </summary>
        Task DeleteAsync(string id, CancellationToken token = default);

        /// <summary>
        /// Enumerate all planning sessions.
        /// </summary>
        Task<List<PlanningSession>> EnumerateAsync(CancellationToken token = default);

        /// <summary>
        /// Enumerate planning sessions reserved by a captain.
        /// </summary>
        Task<List<PlanningSession>> EnumerateByCaptainAsync(string captainId, CancellationToken token = default);

        /// <summary>
        /// Enumerate planning sessions by status.
        /// </summary>
        Task<List<PlanningSession>> EnumerateByStatusAsync(PlanningSessionStatusEnum status, CancellationToken token = default);

        /// <summary>
        /// Read a planning session by tenant and identifier.
        /// </summary>
        Task<PlanningSession?> ReadAsync(string tenantId, string id, CancellationToken token = default);

        /// <summary>
        /// Enumerate planning sessions for a tenant.
        /// </summary>
        Task<List<PlanningSession>> EnumerateAsync(string tenantId, CancellationToken token = default);

        /// <summary>
        /// Read a planning session by tenant, user, and identifier.
        /// </summary>
        Task<PlanningSession?> ReadAsync(string tenantId, string userId, string id, CancellationToken token = default);

        /// <summary>
        /// Enumerate planning sessions for a user within a tenant.
        /// </summary>
        Task<List<PlanningSession>> EnumerateAsync(string tenantId, string userId, CancellationToken token = default);
    }
}
