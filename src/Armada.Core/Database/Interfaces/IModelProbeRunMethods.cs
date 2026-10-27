namespace Armada.Core.Database.Interfaces
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;

    /// <summary>
    /// Database operations for model probe runs (model_probe_runs): one row per probe-only Evaluate.
    /// </summary>
    public interface IModelProbeRunMethods
    {
        /// <summary>
        /// Create a run (its <see cref="ModelProbeRun.Results"/> are not written; use <see cref="IModelProbeResultMethods"/>).
        /// </summary>
        /// <param name="run">Run.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The created run.</returns>
        Task<ModelProbeRun> CreateAsync(ModelProbeRun run, CancellationToken token = default);

        /// <summary>
        /// Read a run by id (any tenant), without its results.
        /// </summary>
        /// <param name="id">Run identifier.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The run, or null.</returns>
        Task<ModelProbeRun?> ReadAsync(string id, CancellationToken token = default);

        /// <summary>
        /// Update every field of a run except its identifier, tenant, user and creation time; sets LastUpdateUtc.
        /// </summary>
        /// <param name="run">Run with updated fields.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The updated run.</returns>
        Task<ModelProbeRun> UpdateAsync(ModelProbeRun run, CancellationToken token = default);

        /// <summary>
        /// Delete a run and its results.
        /// </summary>
        /// <param name="id">Run identifier.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>True when a run row was deleted.</returns>
        Task<bool> DeleteAsync(string id, CancellationToken token = default);

        /// <summary>
        /// List one page of runs (without results), newest (CreatedUtc) first, with the total count of matching rows.
        /// </summary>
        /// <param name="query">Filters and paging.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The page.</returns>
        Task<EnumerationResult<ModelProbeRun>> EnumerateAsync(ModelProbeRunQuery query, CancellationToken token = default);

        /// <summary>
        /// Total estimated tokens used by runs created at or after a time, overall or for one inference host.
        /// </summary>
        /// <param name="sinceUtc">Earliest creation time counted.</param>
        /// <param name="inferenceHost">Only runs of this inference host, or null for every run.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The sum (0 when none).</returns>
        Task<long> SumTokensUsedSinceAsync(DateTime sinceUtc, string? inferenceHost, CancellationToken token = default);
    }
}
