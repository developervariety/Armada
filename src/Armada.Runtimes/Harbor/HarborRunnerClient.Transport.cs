namespace Armada.Runtimes.Harbor
{
    using System;
    using System.IO;
    using System.Net.WebSockets;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Harbor;

    /// <summary>Bounded WebSocket text framing for the Harbor runner.</summary>
    public sealed partial class HarborRunnerClient
    {
        private static async Task SendAsync(ClientWebSocket socket, SemaphoreSlim sendLock, HarborMessage message, CancellationToken token)
        {
            byte[] payload = Encoding.UTF8.GetBytes(HarborProtocol.Serialize(message));
            if (payload.Length > MaximumFrameBytes) throw new InvalidOperationException("harbor_frame_too_large");
            await sendLock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                await socket.SendAsync(new ArraySegment<byte>(payload), WebSocketMessageType.Text, true, token).ConfigureAwait(false);
            }
            finally
            {
                sendLock.Release();
            }
        }

        private static async Task<HarborMessage> ReceiveAsync(ClientWebSocket socket, CancellationToken token)
        {
            byte[] buffer = new byte[16 * 1024];
            using (MemoryStream stream = new MemoryStream())
            {
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close) throw new WebSocketException("harbor_link_closed");
                    if (result.MessageType != WebSocketMessageType.Text) throw new InvalidOperationException("harbor_frame_type_invalid");
                    if (stream.Length + result.Count > MaximumFrameBytes) throw new InvalidOperationException("harbor_frame_too_large");
                    stream.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                return HarborProtocol.Deserialize(Encoding.UTF8.GetString(stream.ToArray()));
            }
        }
    }
}
