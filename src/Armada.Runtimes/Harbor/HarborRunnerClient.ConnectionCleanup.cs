namespace Armada.Runtimes.Harbor
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>Stops and joins all per-connection receive and pump tasks.</summary>
    public sealed partial class HarborRunnerClient
    {
        private async Task JoinConnectionTasksAsync(Task? receive, List<Task> background, CancellationTokenSource connection)
        {
            try
            {
                if (receive != null) await receive.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (connection.IsCancellationRequested)
            {
                _Log("Harbor receive task stopped: harbor_link_closed");
            }
            finally
            {
                try { await Task.WhenAll(background).ConfigureAwait(false); }
                catch (OperationCanceledException) { _Log("Harbor connection tasks stopped: harbor_link_closed"); }
            }
        }
    }
}
