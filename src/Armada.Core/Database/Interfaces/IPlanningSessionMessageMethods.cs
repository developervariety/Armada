namespace Armada.Core.Database.Interfaces
{
    using Armada.Core.Models;

    /// <summary>
    /// Database operations for planning session transcript messages.
    /// </summary>
    public interface IPlanningSessionMessageMethods
    {
        /// <summary>
        /// Create a planning session message.
        /// </summary>
        Task<PlanningSessionMessage> CreateAsync(PlanningSessionMessage message, CancellationToken token = default);

        /// <summary>
        /// Read a planning session message by identifier.
        /// </summary>
        Task<PlanningSessionMessage?> ReadAsync(string id, CancellationToken token = default);

        /// <summary>
        /// Update a planning session message.
        /// </summary>
        Task<PlanningSessionMessage> UpdateAsync(PlanningSessionMessage message, CancellationToken token = default);

        /// <summary>
        /// Delete a planning session message by identifier.
        /// </summary>
        Task DeleteAsync(string id, CancellationToken token = default);

        /// <summary>
        /// Delete all planning session messages for a session.
        /// </summary>
        Task DeleteBySessionAsync(string planningSessionId, CancellationToken token = default);

        /// <summary>
        /// Enumerate planning session messages for a session.
        /// </summary>
        Task<List<PlanningSessionMessage>> EnumerateBySessionAsync(string planningSessionId, CancellationToken token = default);
    }
}
