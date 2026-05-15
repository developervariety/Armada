namespace Armada.Core.Database.Interfaces
{
    using Armada.Core.Models;

    /// <summary>
    /// Database operations for normalized objectives/backlog entries.
    /// </summary>
    public interface IObjectiveMethods
    {
        Task<Objective> CreateAsync(Objective objective, CancellationToken token = default);
        Task<Objective> UpdateAsync(Objective objective, CancellationToken token = default);
        Task<Objective?> ReadAsync(string id, CancellationToken token = default);
        Task<Objective?> ReadAsync(string tenantId, string id, CancellationToken token = default);
        Task<Objective?> ReadAsync(string tenantId, string userId, string id, CancellationToken token = default);
        Task DeleteAsync(string id, CancellationToken token = default);
        Task DeleteAsync(string tenantId, string id, CancellationToken token = default);
        Task<List<Objective>> EnumerateAsync(CancellationToken token = default);
        Task<List<Objective>> EnumerateAsync(string tenantId, CancellationToken token = default);
        Task<List<Objective>> EnumerateAsync(string tenantId, string userId, CancellationToken token = default);
        Task<bool> ExistsAnyAsync(CancellationToken token = default);
        Task<bool> ExistsAsync(string id, CancellationToken token = default);
    }
}
