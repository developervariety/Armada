namespace Armada.Core.Database.Interfaces
{
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;

    /// <summary>
    /// Database methods for releases.
    /// </summary>
    public interface IReleaseMethods
    {
        /// <summary>
        /// Create a release.
        /// </summary>
        Task<Release> CreateAsync(Release release, CancellationToken token = default);

        /// <summary>
        /// Read one release by ID within an optional scope query.
        /// </summary>
        Task<Release?> ReadAsync(string id, ReleaseQuery? query = null, CancellationToken token = default);

        /// <summary>
        /// Update a release.
        /// </summary>
        Task<Release> UpdateAsync(Release release, CancellationToken token = default);

        /// <summary>
        /// Delete a release by ID within an optional scope query.
        /// </summary>
        Task DeleteAsync(string id, ReleaseQuery? query = null, CancellationToken token = default);

        /// <summary>
        /// Enumerate releases with paging and filtering.
        /// </summary>
        Task<EnumerationResult<Release>> EnumerateAsync(ReleaseQuery query, CancellationToken token = default);

        /// <summary>
        /// Enumerate all releases matching the query without paging.
        /// </summary>
        Task<List<Release>> EnumerateAllAsync(ReleaseQuery query, CancellationToken token = default);
    }
}
