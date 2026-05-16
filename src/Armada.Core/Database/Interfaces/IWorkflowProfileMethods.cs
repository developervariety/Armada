namespace Armada.Core.Database.Interfaces
{
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;

    /// <summary>
    /// Database methods for workflow profiles.
    /// </summary>
    public interface IWorkflowProfileMethods
    {
        /// <summary>
        /// Create a workflow profile.
        /// </summary>
        Task<WorkflowProfile> CreateAsync(WorkflowProfile profile, CancellationToken token = default);

        /// <summary>
        /// Read one workflow profile by ID within an optional scope query.
        /// </summary>
        Task<WorkflowProfile?> ReadAsync(string id, WorkflowProfileQuery? query = null, CancellationToken token = default);

        /// <summary>
        /// Update a workflow profile.
        /// </summary>
        Task<WorkflowProfile> UpdateAsync(WorkflowProfile profile, CancellationToken token = default);

        /// <summary>
        /// Delete a workflow profile by ID within an optional scope query.
        /// </summary>
        Task DeleteAsync(string id, WorkflowProfileQuery? query = null, CancellationToken token = default);

        /// <summary>
        /// Enumerate workflow profiles with paging and filtering.
        /// </summary>
        Task<EnumerationResult<WorkflowProfile>> EnumerateAsync(WorkflowProfileQuery query, CancellationToken token = default);

        /// <summary>
        /// Enumerate all workflow profiles matching the query without paging.
        /// </summary>
        Task<List<WorkflowProfile>> EnumerateAllAsync(WorkflowProfileQuery query, CancellationToken token = default);
    }
}
