namespace Test.Shared.Suites.Services
{
    using System;
    using System.Threading;
    using System.Threading.Channels;
    using System.Threading.Tasks;
    using Armada.Core.Harbor;
    using Armada.Core.Services;

    /// <summary>
    /// A Harbor transport to an Admiral that accepts the handshake and acknowledges the first heartbeat, then goes silent
    /// without closing the socket (a half-open link after a lid close): sends still succeed and nothing more arrives.
    /// </summary>
    public sealed class SilentAfterAckTransport : IHarborTransport
    {
        #region Public-Members

        /// <summary>
        /// Whether the client closed the transport.
        /// </summary>
        public bool Closed { get; private set; } = false;

        #endregion

        #region Private-Members

        private readonly Channel<string> _Inbound = Channel.CreateUnbounded<string>();
        private int _HeartbeatsAcknowledged = 0;

        #endregion

        #region Public-Methods

        /// <inheritdoc />
        public Task ConnectAsync(CancellationToken token)
        {
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task SendAsync(string text, CancellationToken token)
        {
            HarborMessage message = HarborProtocol.Deserialize(text);
            if (message is HarborHandshake)
                _Inbound.Writer.TryWrite(HarborProtocol.Serialize(new HarborHandshakeAck { Accepted = true }));
            else if (message is HarborHeartbeat heartbeat && heartbeat.Sequence.HasValue && Interlocked.Increment(ref _HeartbeatsAcknowledged) == 1)
                _Inbound.Writer.TryWrite(HarborProtocol.Serialize(new HarborHeartbeatAck { Sequence = heartbeat.Sequence.Value }));
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public async Task<string?> ReceiveAsync(CancellationToken token)
        {
            if (await _Inbound.Reader.WaitToReadAsync(token).ConfigureAwait(false) && _Inbound.Reader.TryRead(out string? text)) return text;
            return null;
        }

        /// <inheritdoc />
        public Task CloseAsync(CancellationToken token)
        {
            Closed = true;
            return Task.CompletedTask;
        }

        #endregion
    }
}
