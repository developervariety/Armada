namespace Armada.Core.Database.Interfaces
{
    using Armada.Core.Models;

    /// <summary>
    /// Database operations for objective refinement transcript messages.
    /// </summary>
    public interface IObjectiveRefinementMessageMethods
    {
        /// <summary>
        /// Creates a refinement transcript message.
        /// </summary>
        Task<ObjectiveRefinementMessage> CreateAsync(ObjectiveRefinementMessage message, CancellationToken token = default);

        /// <summary>
        /// Updates a refinement transcript message.
        /// </summary>
        Task<ObjectiveRefinementMessage> UpdateAsync(ObjectiveRefinementMessage message, CancellationToken token = default);

        /// <summary>
        /// Reads a refinement transcript message by its identifier.
        /// </summary>
        Task<ObjectiveRefinementMessage?> ReadAsync(string id, CancellationToken token = default);

        /// <summary>
        /// Deletes a refinement transcript message by its identifier.
        /// </summary>
        Task DeleteAsync(string id, CancellationToken token = default);

        /// <summary>
        /// Deletes all refinement transcript messages for a session.
        /// </summary>
        Task DeleteBySessionAsync(string objectiveRefinementSessionId, CancellationToken token = default);

        /// <summary>
        /// Enumerates refinement transcript messages for a session.
        /// </summary>
        Task<List<ObjectiveRefinementMessage>> EnumerateBySessionAsync(string objectiveRefinementSessionId, CancellationToken token = default);

        /// <summary>
        /// Enumerates refinement transcript messages for an objective.
        /// </summary>
        Task<List<ObjectiveRefinementMessage>> EnumerateByObjectiveAsync(string objectiveId, CancellationToken token = default);
    }
}
