namespace Armada.Core.Database.Interfaces
{
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;

    /// <summary>
    /// Database methods for deployments.
    /// </summary>
    public interface IDeploymentMethods
    {
        /// <summary>
        /// Create a deployment.
        /// </summary>
        Task<Deployment> CreateAsync(Deployment deployment, CancellationToken token = default);

        /// <summary>
        /// Read one deployment by ID within an optional scope query.
        /// </summary>
        Task<Deployment?> ReadAsync(string id, DeploymentQuery? query = null, CancellationToken token = default);

        /// <summary>
        /// Update a deployment.
        /// </summary>
        Task<Deployment> UpdateAsync(Deployment deployment, CancellationToken token = default);

        /// <summary>
        /// Delete a deployment by ID within an optional scope query.
        /// </summary>
        Task DeleteAsync(string id, DeploymentQuery? query = null, CancellationToken token = default);

        /// <summary>
        /// Enumerate deployments with paging and filtering.
        /// </summary>
        Task<EnumerationResult<Deployment>> EnumerateAsync(DeploymentQuery query, CancellationToken token = default);

        /// <summary>
        /// Enumerate all deployments matching the query without paging.
        /// </summary>
        Task<List<Deployment>> EnumerateAllAsync(DeploymentQuery query, CancellationToken token = default);
    }
}
