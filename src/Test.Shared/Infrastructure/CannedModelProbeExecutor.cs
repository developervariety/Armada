namespace Test.Shared.Infrastructure
{
    using System;
    using System.Collections.Concurrent;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;

    /// <summary>
    /// Evaluate probe executor for live-server tests: answers every exchange with a canned reply and never launches a
    /// runtime or a model. Set on <c>ArmadaServer.ModelProbeExecutorOverride</c>.
    /// </summary>
    public sealed class CannedModelProbeExecutor : IModelProbeExecutor
    {
        #region Public-Members

        /// <summary>
        /// Builds the reply for one exchange. Default: a short ASCII answer carrying the Worker result marker.
        /// </summary>
        public Func<ModelProbeExchangeRequest, ModelProbeExchange> Answer { get; set; } = request => new ModelProbeExchange
        {
            Output = "Done.\n[ARMADA:RESULT] COMPLETE",
            ExitCode = 0,
            FirstOutputMs = 5,
            DurationMs = 20
        };

        /// <summary>
        /// Every exchange received, in order.
        /// </summary>
        public ConcurrentQueue<ModelProbeExchangeRequest> Requests { get; } = new ConcurrentQueue<ModelProbeExchangeRequest>();

        #endregion

        #region Public-Methods

        /// <summary>
        /// Answer one exchange.
        /// </summary>
        /// <param name="request">The exchange.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The canned reply.</returns>
        /// <exception cref="ArgumentNullException">Thrown when request is null.</exception>
        public Task<ModelProbeExchange> RunAsync(ModelProbeExchangeRequest request, CancellationToken token = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            Requests.Enqueue(request);
            return Task.FromResult(Answer(request));
        }

        #endregion
    }
}
