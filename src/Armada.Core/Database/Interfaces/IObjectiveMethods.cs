namespace Armada.Core.Database.Interfaces
{
    using Armada.Core.Models;

    /// <summary>
    /// Database operations for normalized objectives/backlog entries.
    /// </summary>
    public interface IObjectiveMethods
    {
        /// <summary>
        /// Creates a new objective or backlog entry.
        /// </summary>
        Task<Objective> CreateAsync(Objective objective, CancellationToken token = default);

        /// <summary>
        /// Updates an existing objective or backlog entry.
        /// </summary>
        Task<Objective> UpdateAsync(Objective objective, CancellationToken token = default);

        /// <summary>
        /// Reads an objective by its identifier.
        /// </summary>
        Task<Objective?> ReadAsync(string id, CancellationToken token = default);

        /// <summary>
        /// Reads an objective for a specific tenant.
        /// </summary>
        Task<Objective?> ReadAsync(string tenantId, string id, CancellationToken token = default);

        /// <summary>
        /// Reads an objective for a specific tenant and user.
        /// </summary>
        Task<Objective?> ReadAsync(string tenantId, string userId, string id, CancellationToken token = default);

        /// <summary>
        /// Deletes an objective by its identifier.
        /// </summary>
        Task DeleteAsync(string id, CancellationToken token = default);

        /// <summary>
        /// Deletes an objective for a specific tenant.
        /// </summary>
        Task DeleteAsync(string tenantId, string id, CancellationToken token = default);

        /// <summary>
        /// Enumerates all objectives.
        /// </summary>
        Task<List<Objective>> EnumerateAsync(CancellationToken token = default);

        /// <summary>
        /// Enumerates objectives for a specific tenant.
        /// </summary>
        Task<List<Objective>> EnumerateAsync(string tenantId, CancellationToken token = default);

        /// <summary>
        /// Enumerates objectives for a specific tenant and user.
        /// </summary>
        Task<List<Objective>> EnumerateAsync(string tenantId, string userId, CancellationToken token = default);

        /// <summary>
        /// Determines whether any objectives exist.
        /// </summary>
        Task<bool> ExistsAnyAsync(CancellationToken token = default);

        /// <summary>
        /// Determines whether an objective exists.
        /// </summary>
        Task<bool> ExistsAsync(string id, CancellationToken token = default);
    }
}
