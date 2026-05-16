namespace Armada.Core.Database.Interfaces
{
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;

    /// <summary>
    /// Database methods for deployment environments.
    /// </summary>
    public interface IDeploymentEnvironmentMethods
    {
        /// <summary>
        /// Create a deployment environment.
        /// </summary>
        Task<DeploymentEnvironment> CreateAsync(DeploymentEnvironment environment, CancellationToken token = default);

        /// <summary>
        /// Read one deployment environment by ID within an optional scope query.
        /// </summary>
        Task<DeploymentEnvironment?> ReadAsync(string id, DeploymentEnvironmentQuery? query = null, CancellationToken token = default);

        /// <summary>
        /// Update a deployment environment.
        /// </summary>
        Task<DeploymentEnvironment> UpdateAsync(DeploymentEnvironment environment, CancellationToken token = default);

        /// <summary>
        /// Delete a deployment environment by ID within an optional scope query.
        /// </summary>
        Task DeleteAsync(string id, DeploymentEnvironmentQuery? query = null, CancellationToken token = default);

        /// <summary>
        /// Enumerate deployment environments with paging and filtering.
        /// </summary>
        Task<EnumerationResult<DeploymentEnvironment>> EnumerateAsync(DeploymentEnvironmentQuery query, CancellationToken token = default);

        /// <summary>
        /// Enumerate all deployment environments matching the query without paging.
        /// </summary>
        Task<List<DeploymentEnvironment>> EnumerateAllAsync(DeploymentEnvironmentQuery query, CancellationToken token = default);
    }
}
