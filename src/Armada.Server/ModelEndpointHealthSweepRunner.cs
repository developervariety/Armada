namespace Armada.Server
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>Runs model endpoint health sweeps without overlapping probes.</summary>
    internal static class ModelEndpointHealthSweepRunner
    {
        /// <summary>Run one bounded, serial health sweep until cancellation.</summary>
        /// <param name="sweep">The endpoint sweep.</param>
        /// <param name="interval">The delay between completed sweeps, read after every sweep so a
        /// hot-reloaded heartbeat interval applies without a restart.</param>
        /// <param name="onFailure">The failure observer.</param>
        /// <param name="token">Cancellation token.</param>
        public static async Task RunAsync(
            Func<CancellationToken, Task> sweep,
            Func<TimeSpan> interval,
            Action<Exception> onFailure,
            CancellationToken token)
        {
            if (sweep == null) throw new ArgumentNullException(nameof(sweep));
            if (interval == null) throw new ArgumentNullException(nameof(interval));
            if (onFailure == null) throw new ArgumentNullException(nameof(onFailure));

            while (!token.IsCancellationRequested)
            {
                try
                {
                    await sweep(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    onFailure(exception);
                }

                try
                {
                    await Task.Delay(interval(), token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }
}
