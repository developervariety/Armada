namespace Armada.Core.Database.Interfaces
{
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;

    /// <summary>
    /// Database methods for project profiles.
    /// </summary>
    public interface IProjectProfileMethods
    {
        /// <summary>
        /// Create a project profile.
        /// </summary>
        Task<ProjectProfile> CreateAsync(ProjectProfile profile, CancellationToken token = default);

        /// <summary>
        /// Read one project profile by ID within an optional scope query.
        /// </summary>
        Task<ProjectProfile?> ReadAsync(string id, ProjectProfileQuery? query = null, CancellationToken token = default);

        /// <summary>
        /// Update a project profile.
        /// </summary>
        Task<ProjectProfile> UpdateAsync(ProjectProfile profile, CancellationToken token = default);

        /// <summary>
        /// Delete a project profile by ID within an optional scope query.
        /// </summary>
        Task DeleteAsync(string id, ProjectProfileQuery? query = null, CancellationToken token = default);

        /// <summary>
        /// Enumerate project profiles with paging and filtering.
        /// </summary>
        Task<EnumerationResult<ProjectProfile>> EnumerateAsync(ProjectProfileQuery query, CancellationToken token = default);

        /// <summary>
        /// Enumerate all project profiles matching the query without paging.
        /// </summary>
        Task<List<ProjectProfile>> EnumerateAllAsync(ProjectProfileQuery query, CancellationToken token = default);
    }
}
