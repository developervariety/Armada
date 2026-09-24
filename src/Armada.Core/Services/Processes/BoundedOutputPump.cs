namespace Armada.Core.Services
{
    using System;
    using System.IO;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Reads one process output stream in fixed-size chunks and hands complete lines to a
    /// <see cref="BoundedOutput"/>. A line is held only up to a character bound: when a line grows past it,
    /// its beginning is dropped and counted as omitted while the stream is still being read, so a stream with
    /// no line terminator cannot grow the reader's memory without limit.
    /// </summary>
    /// <remarks>
    /// Line terminators follow <see cref="TextReader.ReadLine"/>: a line ends at "\n", "\r" or "\r\n".
    /// </remarks>
    public static class BoundedOutputPump
    {
        #region Public-Members

        /// <summary>Characters read from the stream per chunk.</summary>
        public const int ChunkChars = 4096;

        #endregion

        #region Public-Methods

        /// <summary>
        /// Copy a stream into the output until end of stream or until the token is cancelled.
        /// </summary>
        /// <param name="reader">Stream reader to drain.</param>
        /// <param name="output">Destination buffer.</param>
        /// <param name="maximumLineChars">Most characters of one line held at once; at least <see cref="ChunkChars"/>.</param>
        /// <param name="token">Stops the copy; the partial line read so far is still delivered.</param>
        /// <returns>A task that completes when the stream ends or the copy is stopped.</returns>
        public static async Task PumpAsync(TextReader reader, BoundedOutput output, int maximumLineChars, CancellationToken token)
        {
            if (reader == null) throw new ArgumentNullException(nameof(reader));
            if (output == null) throw new ArgumentNullException(nameof(output));

            LineBuffer line = new LineBuffer(Math.Max(ChunkChars, maximumLineChars));
            char[] chunk = new char[ChunkChars];
            bool previousWasCarriageReturn = false;
            try
            {
                while (true)
                {
                    int read = await reader.ReadAsync(chunk.AsMemory(), token).ConfigureAwait(false);
                    if (read == 0) break;

                    int start = 0;
                    for (int i = 0; i < read; i++)
                    {
                        char ch = chunk[i];
                        if (ch != '\n' && ch != '\r')
                        {
                            previousWasCarriageReturn = false;
                            continue;
                        }

                        if (ch == '\n' && previousWasCarriageReturn && i == start && line.IsEmpty)
                        {
                            // The "\n" of a "\r\n" pair whose "\r" already ended the line.
                            previousWasCarriageReturn = false;
                            start = i + 1;
                            continue;
                        }

                        line.Append(chunk.AsSpan(start, i - start));
                        line.Deliver(output);
                        previousWasCarriageReturn = ch == '\r';
                        start = i + 1;
                    }

                    line.Append(chunk.AsSpan(start, read - start));
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // The caller stopped the copy after its own deadline; what was read is delivered below.
            }

            if (!line.IsEmpty || line.DroppedBytes > 0) line.Deliver(output);
        }

        /// <summary>
        /// Copy a stream into an exact-text capture until end of stream or until the token is cancelled.
        /// </summary>
        /// <param name="reader">Stream reader to drain.</param>
        /// <param name="capture">Destination capture.</param>
        /// <param name="token">Stops the copy; what was read so far stays in the capture.</param>
        /// <returns>A task that completes when the stream ends or the copy is stopped.</returns>
        public static async Task PumpTextAsync(TextReader reader, BoundedTextCapture capture, CancellationToken token)
        {
            if (reader == null) throw new ArgumentNullException(nameof(reader));
            if (capture == null) throw new ArgumentNullException(nameof(capture));

            char[] chunk = new char[ChunkChars];
            try
            {
                while (true)
                {
                    int read = await reader.ReadAsync(chunk.AsMemory(), token).ConfigureAwait(false);
                    if (read == 0) break;
                    capture.Append(chunk.AsSpan(0, read));
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // The caller stopped the copy after its own deadline; what was read stays in the capture.
            }
        }

        #endregion

        #region Private-Types

        private sealed class LineBuffer
        {
            private readonly char[] _Chars;
            private int _Length = 0;

            public LineBuffer(int capacity)
            {
                _Chars = new char[capacity];
            }

            public long DroppedBytes { get; private set; } = 0;

            public bool IsEmpty => _Length == 0;

            public void Append(ReadOnlySpan<char> text)
            {
                if (text.IsEmpty) return;
                int overflow = _Length + text.Length - _Chars.Length;
                if (overflow > 0)
                {
                    int fromHeld = Math.Min(overflow, _Length);
                    if (fromHeld > 0)
                    {
                        DroppedBytes += Encoding.UTF8.GetByteCount(_Chars.AsSpan(0, fromHeld));
                        Array.Copy(_Chars, fromHeld, _Chars, 0, _Length - fromHeld);
                        _Length -= fromHeld;
                    }

                    int fromText = overflow - fromHeld;
                    if (fromText > 0)
                    {
                        DroppedBytes += Encoding.UTF8.GetByteCount(text.Slice(0, fromText));
                        text = text.Slice(fromText);
                    }
                }

                text.CopyTo(_Chars.AsSpan(_Length));
                _Length += text.Length;
            }

            public void Deliver(BoundedOutput output)
            {
                int start = 0;
                // Never begin a kept line on the second half of a surrogate pair whose first half was dropped.
                if (DroppedBytes > 0 && _Length > 0 && Char.IsLowSurrogate(_Chars[0])) start = 1;
                output.AppendLine(new String(_Chars, start, _Length - start), DroppedBytes);
                _Length = 0;
                DroppedBytes = 0;
            }
        }

        #endregion
    }
}
