namespace Armada.Proxy.Services
{
    using System.Collections.Concurrent;
    using Armada.Core;
    using Armada.Core.Models;

    /// <summary>
    /// Active bidirectional session for a connected Armada instance.
    /// </summary>
    public class RemoteInstanceSession
    {
        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="sender">Envelope sender delegate.</param>
        public RemoteInstanceSession(Func<RemoteTunnelEnvelope, CancellationToken, Task> sender)
        {
            _Sender = sender ?? throw new ArgumentNullException(nameof(sender));
        }

        #endregion

        #region Public-Members

        /// <summary>
        /// Number of pending response waiters.
        /// </summary>
        public int PendingRequestCount => _PendingRequests.Count;

        #endregion

        #region Public-Methods

        /// <summary>
        /// Send an envelope over the session transport.
        /// </summary>
        public async Task SendAsync(RemoteTunnelEnvelope envelope, CancellationToken token)
        {
            await _Sender(envelope, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Send a request and wait for the correlated response.
        /// </summary>
        public async Task<RemoteTunnelEnvelope> SendRequestAsync(string method, object? payload, TimeSpan timeout, CancellationToken token)
        {
            string correlationId = Guid.NewGuid().ToString("N");
            TaskCompletionSource<RemoteTunnelEnvelope> tcs = new TaskCompletionSource<RemoteTunnelEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
            _PendingRequests[correlationId] = tcs;

            try
            {
                await SendAsync(RemoteTunnelProtocol.CreateRequest(method, payload, correlationId), token).ConfigureAwait(false);

                using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeoutSource.CancelAfter(timeout);
                using CancellationTokenRegistration registration = timeoutSource.Token.Register(() =>
                {
                    if (_PendingRequests.TryRemove(correlationId, out TaskCompletionSource<RemoteTunnelEnvelope>? removed))
                    {
                        removed.TrySetException(new TimeoutException("Timed out waiting for tunnel response to " + method + "."));
                    }
                });

                return await tcs.Task.ConfigureAwait(false);
            }
            finally
            {
                _PendingRequests.TryRemove(correlationId, out TaskCompletionSource<RemoteTunnelEnvelope>? _);
            }
        }

        /// <summary>
        /// Complete a pending response if the correlation matches.
        /// </summary>
        public bool TryCompleteResponse(RemoteTunnelEnvelope envelope)
        {
            if (String.IsNullOrWhiteSpace(envelope.CorrelationId))
            {
                return false;
            }

            if (_PendingRequests.TryRemove(envelope.CorrelationId, out TaskCompletionSource<RemoteTunnelEnvelope>? waiter))
            {
                waiter.TrySetResult(envelope);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Fail all pending requests because the transport has gone away.
        /// </summary>
        public void FailAll(Exception ex)
        {
            foreach (KeyValuePair<string, TaskCompletionSource<RemoteTunnelEnvelope>> entry in _PendingRequests.ToArray())
            {
                if (_PendingRequests.TryRemove(entry.Key, out TaskCompletionSource<RemoteTunnelEnvelope>? waiter))
                {
                    waiter.TrySetException(ex);
                }
            }
        }

        #endregion

        #region Private-Members

        private readonly Func<RemoteTunnelEnvelope, CancellationToken, Task> _Sender;
        private readonly ConcurrentDictionary<string, TaskCompletionSource<RemoteTunnelEnvelope>> _PendingRequests = new ConcurrentDictionary<string, TaskCompletionSource<RemoteTunnelEnvelope>>();

        #endregion
    }
}
