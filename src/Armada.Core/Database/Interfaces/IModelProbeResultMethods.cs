namespace Armada.Core.Database.Interfaces
{
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;

    /// <summary>
    /// Database operations for model probe results (model_probe_results): one row per probe of a model probe run.
    /// </summary>
    public interface IModelProbeResultMethods
    {
        /// <summary>
        /// Create a result.
        /// </summary>
        /// <param name="result">Result.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The created result.</returns>
        Task<ModelProbeResult> CreateAsync(ModelProbeResult result, CancellationToken token = default);

        /// <summary>
        /// Every result of a run, in creation order.
        /// </summary>
        /// <param name="runId">Run identifier.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The results (empty when none).</returns>
        Task<List<ModelProbeResult>> ReadByRunAsync(string runId, CancellationToken token = default);

        /// <summary>
        /// Delete every result of a run.
        /// </summary>
        /// <param name="runId">Run identifier.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The number of rows deleted.</returns>
        Task<int> DeleteByRunAsync(string runId, CancellationToken token = default);
    }
}
