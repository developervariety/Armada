namespace Armada.Core.Database.Interfaces
{
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// Database operations for objective refinement sessions.
    /// </summary>
    public interface IObjectiveRefinementSessionMethods
    {
        /// <summary>
        /// Creates an objective refinement session.
        /// </summary>
        Task<ObjectiveRefinementSession> CreateAsync(ObjectiveRefinementSession session, CancellationToken token = default);

        /// <summary>
        /// Updates an objective refinement session.
        /// </summary>
        Task<ObjectiveRefinementSession> UpdateAsync(ObjectiveRefinementSession session, CancellationToken token = default);

        /// <summary>
        /// Reads an objective refinement session by its identifier.
        /// </summary>
        Task<ObjectiveRefinementSession?> ReadAsync(string id, CancellationToken token = default);

        /// <summary>
        /// Reads an objective refinement session for a specific tenant.
        /// </summary>
        Task<ObjectiveRefinementSession?> ReadAsync(string tenantId, string id, CancellationToken token = default);

        /// <summary>
        /// Reads an objective refinement session for a specific tenant and user.
        /// </summary>
        Task<ObjectiveRefinementSession?> ReadAsync(string tenantId, string userId, string id, CancellationToken token = default);

        /// <summary>
        /// Deletes an objective refinement session by its identifier.
        /// </summary>
        Task DeleteAsync(string id, CancellationToken token = default);

        /// <summary>
        /// Enumerates all objective refinement sessions.
        /// </summary>
        Task<List<ObjectiveRefinementSession>> EnumerateAsync(CancellationToken token = default);

        /// <summary>
        /// Enumerates objective refinement sessions for a specific tenant.
        /// </summary>
        Task<List<ObjectiveRefinementSession>> EnumerateAsync(string tenantId, CancellationToken token = default);

        /// <summary>
        /// Enumerates objective refinement sessions for a specific tenant and user.
        /// </summary>
        Task<List<ObjectiveRefinementSession>> EnumerateAsync(string tenantId, string userId, CancellationToken token = default);

        /// <summary>
        /// Enumerates objective refinement sessions for an objective.
        /// </summary>
        Task<List<ObjectiveRefinementSession>> EnumerateByObjectiveAsync(string objectiveId, CancellationToken token = default);

        /// <summary>
        /// Enumerates objective refinement sessions for a captain.
        /// </summary>
        Task<List<ObjectiveRefinementSession>> EnumerateByCaptainAsync(string captainId, CancellationToken token = default);

        /// <summary>
        /// Enumerates objective refinement sessions by status.
        /// </summary>
        Task<List<ObjectiveRefinementSession>> EnumerateByStatusAsync(ObjectiveRefinementSessionStatusEnum status, CancellationToken token = default);
    }
}
