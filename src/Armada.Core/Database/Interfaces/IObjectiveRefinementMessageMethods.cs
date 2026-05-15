namespace Armada.Core.Database.Interfaces
{
    using Armada.Core.Models;

    /// <summary>
    /// Database operations for objective refinement transcript messages.
    /// </summary>
    public interface IObjectiveRefinementMessageMethods
    {
        Task<ObjectiveRefinementMessage> CreateAsync(ObjectiveRefinementMessage message, CancellationToken token = default);
        Task<ObjectiveRefinementMessage> UpdateAsync(ObjectiveRefinementMessage message, CancellationToken token = default);
        Task<ObjectiveRefinementMessage?> ReadAsync(string id, CancellationToken token = default);
        Task DeleteAsync(string id, CancellationToken token = default);
        Task DeleteBySessionAsync(string objectiveRefinementSessionId, CancellationToken token = default);
        Task<List<ObjectiveRefinementMessage>> EnumerateBySessionAsync(string objectiveRefinementSessionId, CancellationToken token = default);
        Task<List<ObjectiveRefinementMessage>> EnumerateByObjectiveAsync(string objectiveId, CancellationToken token = default);
    }
}
