namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Net.WebSockets;
    using System.Text;

    /// <summary>
    /// A Harbor transport over a client WebSocket. The Harbor dials the Admiral (client-to-server), which is
    /// why this is a client socket: it works from behind NAT and from a container-published port without the
    /// Admiral reaching into the host.
    /// </summary>
    public class WebSocketHarborTransport : IHarborTransport, IDisposable
    {
        #region Public-Members

        /// <summary>
        /// Largest message accepted from the Admiral, in bytes. A larger message closes the link with MessageTooBig
        /// instead of growing memory without bound.
        /// </summary>
        public const int MaxMessageBytes = 128 * 1024 * 1024;

        #endregion

        #region Private-Members

        private readonly Uri _ServerUri;
        private readonly IReadOnlyDictionary<string, string>? _Headers;
        private readonly int _ReceiveBufferBytes = 65536;
        private ClientWebSocket? _Socket;
        private bool _Disposed = false;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="serverUri">The Admiral link URI (ws:// or wss://).</param>
        /// <param name="headers">Optional headers to set on the upgrade request (for example auth material).</param>
        public WebSocketHarborTransport(Uri serverUri, IReadOnlyDictionary<string, string>? headers = null)
        {
            _ServerUri = serverUri ?? throw new ArgumentNullException(nameof(serverUri));
            _Headers = headers;
        }

        #endregion

        #region Public-Methods

        /// <inheritdoc />
        public async Task ConnectAsync(CancellationToken token)
        {
            _Socket = new ClientWebSocket();
            if (_Headers != null)
            {
                foreach (KeyValuePair<string, string> header in _Headers)
                    _Socket.Options.SetRequestHeader(header.Key, header.Value);
            }

            await _Socket.ConnectAsync(_ServerUri, token).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task SendAsync(string text, CancellationToken token)
        {
            if (_Socket == null) throw new InvalidOperationException("Transport is not connected.");
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            await _Socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, token).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<string?> ReceiveAsync(CancellationToken token)
        {
            if (_Socket == null) throw new InvalidOperationException("Transport is not connected.");

            // Collect the whole message's bytes and decode once: decoding each receive chunk on its own corrupts any
            // multi-byte UTF-8 character that straddles a chunk boundary.
            byte[] buffer = new byte[_ReceiveBufferBytes];
            using (MemoryStream message = new MemoryStream())
            {
                while (true)
                {
                    WebSocketReceiveResult result = await _Socket.ReceiveAsync(new ArraySegment<byte>(buffer), token).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await _Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, token).ConfigureAwait(false);
                        return null;
                    }

                    if (message.Length + result.Count > MaxMessageBytes)
                    {
                        await _Socket.CloseAsync(WebSocketCloseStatus.MessageTooBig, "message exceeds " + MaxMessageBytes + " bytes", token).ConfigureAwait(false);
                        throw new InvalidDataException("Harbor link message exceeded " + MaxMessageBytes + " bytes.");
                    }

                    message.Write(buffer, 0, result.Count);
                    if (result.EndOfMessage) return DecodeUtf8(message);
                }
            }
        }

        /// <inheritdoc />
        public async Task CloseAsync(CancellationToken token)
        {
            if (_Socket == null) return;
            if (_Socket.State == WebSocketState.Open)
            {
                try
                {
                    await _Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, token).ConfigureAwait(false);
                }
                catch
                {
                }
            }
        }

        /// <summary>
        /// Decode a complete message's bytes as UTF-8.
        /// </summary>
        /// <param name="message">The message bytes.</param>
        /// <returns>The text.</returns>
        /// <exception cref="ArgumentNullException">Thrown when message is null.</exception>
        public static string DecodeUtf8(MemoryStream message)
        {
            if (message == null) throw new ArgumentNullException(nameof(message));
            return Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
        }

        /// <summary>
        /// Dispose.
        /// </summary>
        public void Dispose()
        {
            if (_Disposed) return;
            _Socket?.Dispose();
            _Disposed = true;
            GC.SuppressFinalize(this);
        }

        #endregion
    }
}
