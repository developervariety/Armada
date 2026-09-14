namespace Armada.Runtimes.Tools
{
    using System;
    using System.IO;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>Shared limits and bounded I/O helpers for built-in workspace tools.</summary>
    internal static class ToolExecution
    {
        /// <summary>Create a linked cancellation source with the configured tool timeout.</summary>
        public static CancellationTokenSource CreateTimeoutSource(CancellationToken callerToken)
        {
            int timeout = Math.Clamp(ToolSafetyLimits.DefaultToolTimeoutMs, 1, 300_000);
            CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
            timeoutSource.CancelAfter(timeout);
            return timeoutSource;
        }

        /// <summary>Read a text file while enforcing a byte limit.</summary>
        public static async Task<string> ReadTextFileAsync(string path, CancellationToken token)
        {
            int maximum = Math.Clamp(ToolSafetyLimits.MaxReadFileBytes, 1, 64 * 1024 * 1024);
            using FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            byte[] buffer = new byte[81920];
            using MemoryStream content = new MemoryStream();
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) > 0)
            {
                if (content.Length + read > maximum)
                    throw new ToolSizeLimitException("File input exceeds the configured read limit of " + maximum + " bytes.");
                content.Write(buffer, 0, read);
            }

            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(content.ToArray());
        }

        /// <summary>Detects the first line ending using the bounded asynchronous file reader.</summary>
        public static async Task<string> DetectLineEndingAsync(string path, CancellationToken token)
        {
            int maximum = Math.Clamp(ToolSafetyLimits.MaxReadFileBytes, 1, 64 * 1024 * 1024);
            using FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            byte[] buffer = new byte[81920];
            int total = 0;
            int previous = -1;
            while (total < maximum)
            {
                int count = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, maximum - total)), token).ConfigureAwait(false);
                if (count == 0) break;
                total += count;
                for (int i = 0; i < count; i++)
                {
                    int current = buffer[i];
                    if (current == '\n') return previous == '\r' ? "\r\n" : "\n";
                    if (current == '\r')
                    {
                        if (i + 1 < count)
                            return buffer[i + 1] == '\n' ? "\r\n" : "\r";
                        if (total < maximum)
                        {
                            byte[] nextBuffer = new byte[1];
                            int nextCount = await stream.ReadAsync(nextBuffer.AsMemory(), token).ConfigureAwait(false);
                            if (nextCount == 1 && nextBuffer[0] == '\n') return "\r\n";
                            return "\r";
                        }
                        return "\r";
                    }
                    previous = current;
                }
            }

            if (total >= maximum)
                throw new ToolSizeLimitException("File input exceeds the configured read limit of " + maximum + " bytes.");
            return Environment.NewLine;
        }

        /// <summary>Writes UTF-8 content through a same-directory temporary file and atomically replaces the destination.</summary>
        public static async Task AtomicWriteTextFileAsync(string path, string content, CancellationToken token)
        {
            string directory = Path.GetDirectoryName(path) ?? throw new IOException("The file has no parent directory.");
            Directory.CreateDirectory(directory);
            string temporary = Path.Combine(directory, ".armada-write-" + Guid.NewGuid().ToString("N") + ".tmp");
            UnixFileMode? existingMode = null;
            if (!OperatingSystem.IsWindows() && File.Exists(path))
            {
                existingMode = File.GetUnixFileMode(path);
            }

            try
            {
                byte[] bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetBytes(content);
                using (FileStream stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    await stream.WriteAsync(bytes.AsMemory(), token).ConfigureAwait(false);
                    await stream.FlushAsync(token).ConfigureAwait(false);
                }
                token.ThrowIfCancellationRequested();

                if (existingMode.HasValue)
                {
                    File.SetUnixFileMode(temporary, existingMode.Value);
                }

                token.ThrowIfCancellationRequested();
                if (File.Exists(path))
                    File.Replace(temporary, path, destinationBackupFileName: null);
                else
                    File.Move(temporary, path);
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); } catch (Exception) { }
            }
        }

        /// <summary>Reject text input that exceeds the configured byte limit.</summary>
        public static void EnsureInputSize(string value, string name)
        {
            int maximum = Math.Clamp(ToolSafetyLimits.MaxToolInputBytes, 1, 64 * 1024 * 1024);
            if (Encoding.UTF8.GetByteCount(value) > maximum)
                throw new ToolSizeLimitException(name + " exceeds the configured input limit of " + maximum + " bytes.");
        }

        /// <summary>Limit a tool response by UTF-8 bytes and add an explicit truncation marker.</summary>
        public static string LimitOutput(string value)
        {
            return LimitOutput(value, ToolSafetyLimits.MaxToolOutputBytes);
        }

        /// <summary>Limit text using an explicit UTF-8 byte maximum.</summary>
        public static string LimitOutput(string value, int maximumBytes)
        {
            int maximum = Math.Clamp(maximumBytes, 1, 64 * 1024 * 1024);
            if (Encoding.UTF8.GetByteCount(value) <= maximum) return value;
            int low = 0;
            int high = value.Length;
            while (low < high)
            {
                int middle = low + ((high - low + 1) / 2);
                if (Encoding.UTF8.GetByteCount(value.AsSpan(0, middle)) <= maximum)
                    low = middle;
                else
                    high = middle - 1;
            }

            int length = low;
            if (length > 0 && Char.IsHighSurrogate(value[length - 1])) length--;
            return value.Substring(0, length) + "\n[output truncated at " + maximum + " bytes]";
        }

        /// <summary>Create a standard cancellation result without exposing exception text.</summary>
        public static ToolResult Cancelled(string toolCallId, CancellationToken callerToken)
        {
            return new ToolResult
            {
                ToolCallId = toolCallId,
                Success = false,
                Content = System.Text.Json.JsonSerializer.Serialize(new
                {
                    error = callerToken.IsCancellationRequested ? "cancelled" : "timed_out",
                    message = callerToken.IsCancellationRequested ? "Tool execution was cancelled." : "Tool execution exceeded its time limit."
                })
            };
        }
    }

}
