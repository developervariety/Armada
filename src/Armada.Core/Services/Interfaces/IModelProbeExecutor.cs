namespace Armada.Core.Services.Interfaces
{
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;

    /// <summary>
    /// Runs one probe prompt through a captain's own runtime and tool loop (on a Harbor as a ModelProbe job when the
    /// captain routes there), the same way the captain runs in production. The Admiral's implementation uses the captain
    /// launch router; tests supply canned answers.
    /// </summary>
    public interface IModelProbeExecutor
    {
        /// <summary>
        /// Run one exchange. Never throws for a launch failure: it returns
        /// <see cref="ModelProbeExchange.CouldNotRunReason"/> instead.
        /// </summary>
        /// <param name="request">Exchange.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>What the exchange produced.</returns>
        Task<ModelProbeExchange> RunAsync(ModelProbeExchangeRequest request, CancellationToken token = default);
    }
}
