namespace Armada.Runtimes.Harbor
{
    using System;
    using System.IO;
    using System.Text;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Services;

    /// <summary>Captures bounded UTF-8 process output.</summary>
    public sealed partial class HarborRunnerWorkspace
    {
        private static async Task<HarborWorkspaceStreamResult> ReadBoundedAsync(Stream stream, int maximumBytes)
        {
            char[] buffer = new char[8192];
            long totalBytesRead = 0;
            BoundedTextCapture capture = new BoundedTextCapture(maximumBytes, BoundedOutputShapeEnum.HeadAndTail);
            using (StreamReader reader = new StreamReader(stream, new UTF8Encoding(false, false), false, 8192, leaveOpen: true))
            {
                char pendingHighSurrogate = '\0';
                while (true)
                {
                    int read = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                    if (read == 0) break;
                    string chunk = new string(buffer, 0, read);
                    if (pendingHighSurrogate != '\0')
                    {
                        chunk = pendingHighSurrogate + chunk;
                        pendingHighSurrogate = '\0';
                    }
                    if (chunk.Length > 0 && Char.IsHighSurrogate(chunk[chunk.Length - 1]))
                    {
                        pendingHighSurrogate = chunk[chunk.Length - 1];
                        chunk = chunk.Substring(0, chunk.Length - 1);
                    }
                    if (chunk.Length == 0) continue;
                    totalBytesRead = checked(totalBytesRead + Encoding.UTF8.GetByteCount(chunk));
                    capture.Append(chunk.AsSpan());
                }
                if (pendingHighSurrogate != '\0')
                {
                    string tail = pendingHighSurrogate.ToString();
                    totalBytesRead = checked(totalBytesRead + Encoding.UTF8.GetByteCount(tail));
                    capture.Append(tail.AsSpan());
                }
                string text = capture.Render();
                return new HarborWorkspaceStreamResult
                {
                    Bytes = Encoding.UTF8.GetBytes(text), TotalBytesRead = totalBytesRead,
                    OmittedBytes = capture.OmittedBytes, Text = text, Truncated = capture.Truncated
                };
            }
        }

    }
}
