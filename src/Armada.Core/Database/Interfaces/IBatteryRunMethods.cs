namespace Armada.Core.Database.Interfaces
{
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;

    /// <summary>
    /// Database operations for battery runs (battery_runs): one row per run of the fixture battery.
    /// </summary>
    public interface IBatteryRunMethods
    {
        /// <summary>
        /// Create a run.
        /// </summary>
        /// <param name="run">Run.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The created run.</returns>
        Task<BatteryRun> CreateAsync(BatteryRun run, CancellationToken token = default);

        /// <summary>
        /// Read a run without its results.
        /// </summary>
        /// <param name="id">Run identifier.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The run, or null.</returns>
        Task<BatteryRun?> ReadAsync(string id, CancellationToken token = default);

        /// <summary>
        /// Update a run's status, counts, provenance and timestamps.
        /// </summary>
        /// <param name="run">Run.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The updated run.</returns>
        Task<BatteryRun> UpdateAsync(BatteryRun run, CancellationToken token = default);

        /// <summary>
        /// Delete a run and its results.
        /// </summary>
        /// <param name="id">Run identifier.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>True when a run was deleted.</returns>
        Task<bool> DeleteAsync(string id, CancellationToken token = default);

        /// <summary>
        /// Runs matching a query, newest first.
        /// </summary>
        /// <param name="query">Query.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>One page of runs, without their results.</returns>
        Task<EnumerationResult<BatteryRun>> EnumerateAsync(BatteryRunQuery query, CancellationToken token = default);
    }
}
