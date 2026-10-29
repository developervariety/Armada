namespace Armada.Core.Database.Interfaces
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;

    /// <summary>Fixture battery runs, one record per run with the grade of each task.</summary>
    public interface IBatteryRunMethods
    {
        /// <summary>Store a new run.</summary>
        /// <param name="run">Run to store.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The stored run.</returns>
        Task<BatteryRun> CreateAsync(BatteryRun run, CancellationToken token = default);

        /// <summary>Replace the stored status, error, completion time and task results of a run.</summary>
        /// <param name="run">Run to update.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The run.</returns>
        Task<BatteryRun> UpdateAsync(BatteryRun run, CancellationToken token = default);

        /// <summary>Read one run by its identifier.</summary>
        /// <param name="id">Run identifier.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The run, or null if absent.</returns>
        Task<BatteryRun?> ReadAsync(string id, CancellationToken token = default);

        /// <summary>Read the newest runs, optionally for one captain or one pack.</summary>
        /// <param name="captainId">Exact captain identifier, or null for any.</param>
        /// <param name="packId">Exact pack identifier, or null for any.</param>
        /// <param name="limit">Maximum rows to return, clamped to [1, 200].</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The newest runs first.</returns>
        Task<List<BatteryRun>> EnumerateAsync(string? captainId, string? packId, int limit = 50, CancellationToken token = default);

        /// <summary>Mark every run that is still Running as Interrupted.</summary>
        /// <param name="reason">Error text recorded on each run.</param>
        /// <param name="endedUtc">UTC time the runs ended.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The number of runs changed.</returns>
        Task<int> InterruptRunningAsync(string reason, DateTime endedUtc, CancellationToken token = default);
    }
}
