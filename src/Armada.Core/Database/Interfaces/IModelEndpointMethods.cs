namespace Armada.Core.Database.Interfaces
{
    using Armada.Core.Models;

    /// <summary>
    /// Database operations for managed model endpoints (embedding and inference).
    /// </summary>
    public interface IModelEndpointMethods
    {
        /// <summary>
        /// Creates a new model endpoint.
        /// </summary>
        Task<ModelEndpoint> CreateAsync(ModelEndpoint endpoint, CancellationToken token = default);

        /// <summary>
        /// Updates an existing model endpoint.
        /// </summary>
        Task<ModelEndpoint> UpdateAsync(ModelEndpoint endpoint, CancellationToken token = default);

        /// <summary>
        /// Writes health columns and <c>last_update_utc</c> when the stored update time still equals
        /// <paramref name="expectedLastUpdateUtc"/>. Configuration columns are left unchanged. The expected
        /// time is the value read for this result, in the same form the provider already uses for
        /// <c>last_update_utc</c>.
        /// </summary>
        /// <param name="endpoint">Endpoint whose health fields and new <see cref="ModelEndpoint.LastUpdateUtc"/> are written.</param>
        /// <param name="expectedLastUpdateUtc">Update time the stored row must still have.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>True when one row changed. False when the stored update time no longer matches.</returns>
        Task<bool> UpdateHealthAsync(ModelEndpoint endpoint, DateTime expectedLastUpdateUtc, CancellationToken token = default);

        /// <summary>
        /// Reads a model endpoint by its identifier.
        /// </summary>
        Task<ModelEndpoint?> ReadAsync(string id, CancellationToken token = default);

        /// <summary>
        /// Reads a model endpoint for a specific tenant.
        /// </summary>
        Task<ModelEndpoint?> ReadAsync(string tenantId, string id, CancellationToken token = default);

        /// <summary>
        /// Reads a model endpoint for a specific tenant and user.
        /// </summary>
        Task<ModelEndpoint?> ReadAsync(string tenantId, string userId, string id, CancellationToken token = default);

        /// <summary>
        /// Deletes a model endpoint by its identifier.
        /// </summary>
        Task DeleteAsync(string id, CancellationToken token = default);

        /// <summary>
        /// Deletes a model endpoint for a specific tenant.
        /// </summary>
        Task DeleteAsync(string tenantId, string id, CancellationToken token = default);

        /// <summary>
        /// Enumerates all model endpoints (newest first).
        /// </summary>
        Task<List<ModelEndpoint>> EnumerateAsync(CancellationToken token = default);

        /// <summary>
        /// Enumerates model endpoints for a specific tenant (newest first).
        /// </summary>
        Task<List<ModelEndpoint>> EnumerateAsync(string tenantId, CancellationToken token = default);

        /// <summary>
        /// Enumerates model endpoints for a specific tenant and user (newest first).
        /// </summary>
        Task<List<ModelEndpoint>> EnumerateAsync(string tenantId, string userId, CancellationToken token = default);

        /// <summary>
        /// Whether any model endpoint exists.
        /// </summary>
        Task<bool> ExistsAnyAsync(CancellationToken token = default);

        /// <summary>
        /// Whether a model endpoint with the given id exists.
        /// </summary>
        Task<bool> ExistsAsync(string id, CancellationToken token = default);
    }
}
