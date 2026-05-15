namespace Armada.Core.Database.Interfaces
{
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// Database operations for objective refinement sessions.
    /// </summary>
    public interface IObjectiveRefinementSessionMethods
    {
        Task<ObjectiveRefinementSession> CreateAsync(ObjectiveRefinementSession session, CancellationToken token = default);
        Task<ObjectiveRefinementSession> UpdateAsync(ObjectiveRefinementSession session, CancellationToken token = default);
        Task<ObjectiveRefinementSession?> ReadAsync(string id, CancellationToken token = default);
        Task<ObjectiveRefinementSession?> ReadAsync(string tenantId, string id, CancellationToken token = default);
        Task<ObjectiveRefinementSession?> ReadAsync(string tenantId, string userId, string id, CancellationToken token = default);
        Task DeleteAsync(string id, CancellationToken token = default);
        Task<List<ObjectiveRefinementSession>> EnumerateAsync(CancellationToken token = default);
        Task<List<ObjectiveRefinementSession>> EnumerateAsync(string tenantId, CancellationToken token = default);
        Task<List<ObjectiveRefinementSession>> EnumerateAsync(string tenantId, string userId, CancellationToken token = default);
        Task<List<ObjectiveRefinementSession>> EnumerateByObjectiveAsync(string objectiveId, CancellationToken token = default);
        Task<List<ObjectiveRefinementSession>> EnumerateByCaptainAsync(string captainId, CancellationToken token = default);
        Task<List<ObjectiveRefinementSession>> EnumerateByStatusAsync(ObjectiveRefinementSessionStatusEnum status, CancellationToken token = default);
    }
}
