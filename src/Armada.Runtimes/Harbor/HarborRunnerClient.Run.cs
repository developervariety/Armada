namespace Armada.Runtimes.Harbor
{
    using System;
    using System.IO;
    using System.Net.WebSockets;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>Connection lifecycle and reconnect handling for the Harbor runner.</summary>
    public sealed partial class HarborRunnerClient
    {
        /// <summary>Run until cancellation, reconnecting after link failures.</summary>
        /// <param name="token">Cancellation token. Cancellation closes the link and leaves job supervisors running.</param>
        public async Task RunAsync(CancellationToken token = default)
        {
            using (FileStream runnerLock = AcquireRunnerLock())
            {
                while (!token.IsCancellationRequested)
                {
                    try { await ConnectOnceAsync(token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
                    catch (HarborJobJournalException exception)
                    {
                        _Log("Harbor runner stopped: " + exception.ReasonCode);
                        throw;
                    }
                    catch (InvalidOperationException exception) { _Log("Harbor link reconnect: " + SafeReason(exception.Message)); }
                    catch (Exception exception) when (exception is WebSocketException || exception is IOException || exception is ArgumentException || exception is FormatException)
                    {
                        _Log("Harbor link reconnect: harbor_link_connection_failed");
                    }
                    try { await DelayBeforeReconnectAsync(token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
                }
            }
        }

        private async Task ConnectOnceAsync(CancellationToken token)
        {
            using (ClientWebSocket socket = new ClientWebSocket())
            {
                socket.Options.SetRequestHeader("Authorization", "Bearer " + _Settings.Credential);
                await socket.ConnectAsync(new Uri(_Settings.LinkUri, UriKind.Absolute), token).ConfigureAwait(false);
                await RunConnectedAsync(socket, token).ConfigureAwait(false);
            }
        }

        private async Task DelayBeforeReconnectAsync(CancellationToken token)
        {
            await Task.Delay(TimeSpan.FromSeconds(_Settings.ReconnectDelaySeconds), token).ConfigureAwait(false);
        }
    }
}
