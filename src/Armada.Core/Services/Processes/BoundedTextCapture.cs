namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Text;
    using Armada.Core.Enums;

    /// <summary>
    /// Holds the text of one process output stream within a byte budget, exactly as it was written. Output
    /// within the budget is returned unchanged, line terminators included. Past the budget the beginning is
    /// kept and, for <see cref="BoundedOutputShapeEnum.HeadAndTail"/>, a rolling end as well; everything
    /// between them is counted as omitted and named by a marker when the text is rendered.
    /// </summary>
    /// <remarks>
    /// Not thread-safe: each stream has its own capture, fed by one reader.
    /// </remarks>
    public sealed class BoundedTextCapture
    {
        #region Public-Members

        /// <summary>Whether any output was dropped.</summary>
        public bool Truncated => _OmittedBytes > 0;

        /// <summary>UTF-8 bytes of output dropped.</summary>
        public long OmittedBytes => _OmittedBytes;

        /// <summary>The budget in UTF-8 bytes.</summary>
        public int LimitBytes => _LimitBytes;

        /// <summary>Which part of an over-budget stream is kept.</summary>
        public BoundedOutputShapeEnum Shape => _Shape;

        #endregion

        #region Private-Members

        private readonly int _LimitBytes;
        private readonly BoundedOutputShapeEnum _Shape;
        private readonly int _HeadLimit;
        private readonly int _TailLimit;
        private readonly StringBuilder _Head = new StringBuilder();
        private readonly LinkedList<string> _Tail = new LinkedList<string>();
        private int _HeadBytes = 0;
        private long _TailBytes = 0;
        private bool _HeadFull = false;
        private long _OmittedBytes = 0;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Create a capture holding at most the given number of UTF-8 bytes.
        /// </summary>
        /// <param name="limitBytes">Byte budget; clamped to at least 1024.</param>
        /// <param name="shape">Which part of an over-budget stream to keep.</param>
        public BoundedTextCapture(int limitBytes, BoundedOutputShapeEnum shape)
        {
            _LimitBytes = Math.Max(1024, limitBytes);
            _Shape = shape;
            if (shape == BoundedOutputShapeEnum.Head)
            {
                _HeadLimit = _LimitBytes;
                _TailLimit = 0;
            }
            else
            {
                // A fifth for the beginning and the rest for the end: a failing command explains itself last.
                _HeadLimit = _LimitBytes / 5;
                _TailLimit = _LimitBytes - _HeadLimit;
            }
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Add text read from the stream.
        /// </summary>
        /// <param name="text">Text in the order it was read.</param>
        public void Append(ReadOnlySpan<char> text)
        {
            if (text.IsEmpty) return;

            if (!_HeadFull)
            {
                int fits = CharsWithinBytes(text, _HeadLimit - _HeadBytes, fromEnd: false, out int fitBytes);
                _Head.Append(text.Slice(0, fits));
                _HeadBytes += fitBytes;
                if (fits == text.Length) return;
                _HeadFull = true;
                text = text.Slice(fits);
            }

            int bytes = Encoding.UTF8.GetByteCount(text);
            if (_TailLimit == 0)
            {
                _OmittedBytes += bytes;
                return;
            }

            _Tail.AddLast(text.ToString());
            _TailBytes += bytes;
            TrimTail();
        }

        /// <summary>
        /// The kept text. Within the budget this is the stream exactly; past it, the kept beginning, the marker,
        /// and the kept end.
        /// </summary>
        /// <param name="marker">Text placed where output was dropped; null for the default marker naming the byte count.</param>
        /// <returns>The kept text.</returns>
        public string Render(string? marker = null)
        {
            StringBuilder rendered = new StringBuilder(_Head.Length + 128);
            rendered.Append(_Head);
            if (_OmittedBytes > 0) rendered.Append(marker ?? DefaultMarker(_OmittedBytes, _Shape));
            foreach (string chunk in _Tail) rendered.Append(chunk);
            return rendered.ToString();
        }

        /// <summary>
        /// The default omission marker.
        /// </summary>
        /// <param name="omittedBytes">Bytes dropped.</param>
        /// <param name="shape">Which part was kept.</param>
        /// <returns>The marker text, on its own line.</returns>
        public static string DefaultMarker(long omittedBytes, BoundedOutputShapeEnum shape)
        {
            return shape == BoundedOutputShapeEnum.Head
                ? "\n[... " + omittedBytes + " bytes of output omitted; the beginning is kept ...]\n"
                : "\n[... " + omittedBytes + " bytes of output omitted; the beginning and the end are kept ...]\n";
        }

        #endregion

        #region Private-Methods

        private void TrimTail()
        {
            while (_TailBytes > _TailLimit && _Tail.First != null)
            {
                string first = _Tail.First.Value;
                int firstBytes = Encoding.UTF8.GetByteCount(first);
                long excess = _TailBytes - _TailLimit;
                if (firstBytes <= excess)
                {
                    _Tail.RemoveFirst();
                    _TailBytes -= firstBytes;
                    _OmittedBytes += firstBytes;
                    continue;
                }

                // Keep the end of the oldest chunk: drop at least `excess` bytes from its start, on a character boundary.
                int keepChars = CharsWithinBytes(first.AsSpan(), (int)(firstBytes - excess), fromEnd: true, out int keptBytes);
                _Tail.First.Value = first.Substring(first.Length - keepChars);
                _TailBytes -= firstBytes - keptBytes;
                _OmittedBytes += firstBytes - keptBytes;
            }
        }

        /// <summary>
        /// Count the characters from one end of the text whose UTF-8 encoding fits in the byte budget, never
        /// splitting a surrogate pair.
        /// </summary>
        private static int CharsWithinBytes(ReadOnlySpan<char> text, int budget, bool fromEnd, out int usedBytes)
        {
            usedBytes = 0;
            if (budget <= 0) return 0;

            int count = 0;
            while (count < text.Length)
            {
                int index = fromEnd ? text.Length - 1 - count : count;
                char ch = text[index];
                int width = 1;
                int bytes;
                bool pair = fromEnd
                    ? Char.IsLowSurrogate(ch) && index > 0 && Char.IsHighSurrogate(text[index - 1])
                    : Char.IsHighSurrogate(ch) && index + 1 < text.Length && Char.IsLowSurrogate(text[index + 1]);
                if (pair)
                {
                    width = 2;
                    bytes = 4;
                }
                else if (ch < 0x80) bytes = 1;
                else if (ch < 0x800) bytes = 2;
                else bytes = 3;

                if (usedBytes + bytes > budget) break;
                usedBytes += bytes;
                count += width;
            }

            return count;
        }

        #endregion
    }
}
