namespace Armada.Server.WebSocket
{
    using System;
    using System.Threading;
    using System.Threading.Channels;
    using System.Threading.Tasks;

    /// <summary>
    /// Serializes one WebSocket client's outbound messages without blocking producers.
    /// </summary>
    internal sealed class WebSocketClientOutputQueue : IAsyncDisposable
    {
        private readonly Channel<string> _Messages;
        private readonly Func<string, CancellationToken, Task> _SendAsync;
        private readonly CancellationTokenSource _Cancellation = new CancellationTokenSource();
        private readonly Task _Pump;
        private int _Stopping;

        /// <summary>
        /// Raised once when the sender loop stops. The argument contains a send failure, if one occurred.
        /// </summary>
        public event Action<Exception?>? Terminated;

        /// <summary>
        /// Instantiate a bounded per-client output queue.
        /// </summary>
        /// <param name="capacity">Maximum number of messages waiting behind the active send.</param>
        /// <param name="sendAsync">Ordered transport send callback.</param>
        public WebSocketClientOutputQueue(int capacity, Func<string, CancellationToken, Task> sendAsync)
        {
            if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
            _SendAsync = sendAsync ?? throw new ArgumentNullException(nameof(sendAsync));
            _Messages = Channel.CreateBounded<string>(new BoundedChannelOptions(capacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
                AllowSynchronousContinuations = false
            });
            _Pump = PumpAsync();
        }

        /// <summary>
        /// Sender-loop completion, exposed for deterministic shutdown and tests.
        /// </summary>
        public Task Completion => _Pump;

        /// <summary>
        /// Try to append a message without waiting for queue capacity.
        /// </summary>
        public bool TryEnqueue(string message)
        {
            if (message == null) throw new ArgumentNullException(nameof(message));
            if (Volatile.Read(ref _Stopping) != 0) return false;
            return _Messages.Writer.TryWrite(message);
        }

        /// <summary>
        /// Stop the sender loop and discard any messages that the client can no longer receive.
        /// </summary>
        public void Stop()
        {
            if (Interlocked.Exchange(ref _Stopping, 1) != 0) return;
            _Messages.Writer.TryComplete();
            _Cancellation.Cancel();
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            Stop();
            await _Pump.ConfigureAwait(false);
            _Cancellation.Dispose();
        }

        private async Task PumpAsync()
        {
            Exception? failure = null;
            try
            {
                await foreach (string message in _Messages.Reader.ReadAllAsync(_Cancellation.Token).ConfigureAwait(false))
                    await _SendAsync(message, _Cancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_Cancellation.IsCancellationRequested)
            {
                // The owning WebSocket session ended or was disconnected after overflow.
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                Interlocked.Exchange(ref _Stopping, 1);
                _Messages.Writer.TryComplete();
                Terminated?.Invoke(failure);
            }
        }
    }
}
