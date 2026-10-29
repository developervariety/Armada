namespace Armada.Core.Services.Interfaces
{
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Services;

    /// <summary>
    /// Runs one battery task through real dispatch: a voyage on the fixture vessel, pinned to the captain, then waits
    /// for the mission to finish. Tests replace it with a fake that dispatches nothing.
    /// </summary>
    public interface IBatteryTaskDispatcher
    {
        /// <summary>
        /// Dispatch the task and wait for it.
        /// </summary>
        /// <param name="request">Request.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>What happened. Dispatch failures are reported in the result, not thrown.</returns>
        Task<BatteryDispatchResult> RunAsync(BatteryDispatchRequest request, CancellationToken token = default);
    }
}
