namespace Armada.Core.Database.Interfaces
{
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// Database operations for durable landing jobs.
    /// </summary>
    public interface ILandingJobMethods
    {
        /// <summary>
        /// Create a landing job.
        /// </summary>
        Task<LandingJob> CreateAsync(LandingJob job, CancellationToken token = default);

        /// <summary>
        /// Read a landing job by identifier.
        /// </summary>
        Task<LandingJob?> ReadAsync(string id, CancellationToken token = default);

        /// <summary>
        /// Read a landing job by merge entry identifier.
        /// </summary>
        Task<LandingJob?> ReadByMergeEntryAsync(string mergeEntryId, CancellationToken token = default);

        /// <summary>
        /// Update a landing job.
        /// </summary>
        Task<LandingJob> UpdateAsync(LandingJob job, CancellationToken token = default);

        /// <summary>
        /// Delete a landing job by merge entry identifier.
        /// </summary>
        Task DeleteByMergeEntryAsync(string mergeEntryId, CancellationToken token = default);

        /// <summary>
        /// Enumerate landing jobs by durable state.
        /// </summary>
        Task<List<LandingJob>> EnumerateByStateAsync(LandingJobStateEnum state, CancellationToken token = default);
    }
}
