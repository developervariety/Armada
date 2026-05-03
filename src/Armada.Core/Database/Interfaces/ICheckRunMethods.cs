namespace Armada.Core.Database.Interfaces
{
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;

    /// <summary>
    /// Database methods for structured check runs.
    /// </summary>
    public interface ICheckRunMethods
    {
        /// <summary>
        /// Create a structured check run.
        /// </summary>
        Task<CheckRun> CreateAsync(CheckRun checkRun, CancellationToken token = default);

        /// <summary>
        /// Read one structured check run by ID within an optional scope query.
        /// </summary>
        Task<CheckRun?> ReadAsync(string id, CheckRunQuery? query = null, CancellationToken token = default);

        /// <summary>
        /// Update a structured check run.
        /// </summary>
        Task<CheckRun> UpdateAsync(CheckRun checkRun, CancellationToken token = default);

        /// <summary>
        /// Delete a structured check run by ID within an optional scope query.
        /// </summary>
        Task DeleteAsync(string id, CheckRunQuery? query = null, CancellationToken token = default);

        /// <summary>
        /// Enumerate structured check runs with paging and filtering.
        /// </summary>
        Task<EnumerationResult<CheckRun>> EnumerateAsync(CheckRunQuery query, CancellationToken token = default);
    }
}
