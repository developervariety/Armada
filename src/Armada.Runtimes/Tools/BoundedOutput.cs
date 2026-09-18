namespace Armada.Runtimes.Tools
{
    using System;
    using System.Collections.Generic;
    using System.Text;

    /// <summary>
    /// Collects process output line by line within a byte budget, keeping the beginning and the end and
    /// counting what was dropped between them. Safe to append from the standard-output and standard-error
    /// readers at once.
    /// </summary>
    /// <remarks>
    /// Keeping only the beginning, as a plain truncation does, is the wrong shape for command output: a test
    /// runner prints its failures and its totals LAST, so a head-only cut would hand a Judge the progress dots
    /// and hide the verdict it needs. A fifth of the budget holds the head and the rest a rolling tail.
    /// </remarks>
    internal sealed class BoundedOutput
    {
        #region Public-Members

        /// <summary>Whether any output was dropped.</summary>
        public bool Truncated
        {
            get { lock (_Lock) { return _OmittedBytes > 0; } }
        }

        /// <summary>Bytes of output dropped between the head and the tail.</summary>
        public long OmittedBytes
        {
            get { lock (_Lock) { return _OmittedBytes; } }
        }

        #endregion

        #region Private-Members

        private readonly object _Lock = new object();
        private readonly int _HeadLimit;
        private readonly int _TailLimit;
        private readonly StringBuilder _Head = new StringBuilder();
        private readonly LinkedList<string> _Tail = new LinkedList<string>();
        private int _HeadBytes = 0;
        private int _TailBytes = 0;
        private bool _HeadFull = false;
        private long _OmittedBytes = 0;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Create a buffer holding at most the given number of UTF-8 bytes.
        /// </summary>
        /// <param name="limitBytes">Total byte budget; clamped to at least 1024.</param>
        public BoundedOutput(int limitBytes)
        {
            int limit = Math.Max(1024, limitBytes);
            _HeadLimit = limit / 5;
            _TailLimit = limit - _HeadLimit;
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Add one line of output.
        /// </summary>
        /// <param name="line">Line text, without its terminator.</param>
        public void AppendLine(string line)
        {
            string text = (line ?? String.Empty) + "\n";
            int bytes = Encoding.UTF8.GetByteCount(text);

            lock (_Lock)
            {
                if (!_HeadFull && _HeadBytes + bytes <= _HeadLimit)
                {
                    _Head.Append(text);
                    _HeadBytes += bytes;
                    return;
                }

                _HeadFull = true;

                // A single line larger than the whole tail budget is kept as its own last bytes.
                if (bytes > _TailLimit)
                {
                    foreach (string dropped in _Tail) _OmittedBytes += Encoding.UTF8.GetByteCount(dropped);
                    _Tail.Clear();
                    _TailBytes = 0;
                    string kept = TakeLastBytes(text, _TailLimit);
                    _OmittedBytes += bytes - Encoding.UTF8.GetByteCount(kept);
                    _Tail.AddLast(kept);
                    _TailBytes = Encoding.UTF8.GetByteCount(kept);
                    return;
                }

                _Tail.AddLast(text);
                _TailBytes += bytes;
                while (_TailBytes > _TailLimit && _Tail.First != null)
                {
                    int firstBytes = Encoding.UTF8.GetByteCount(_Tail.First.Value);
                    _Tail.RemoveFirst();
                    _TailBytes -= firstBytes;
                    _OmittedBytes += firstBytes;
                }
            }
        }

        /// <summary>
        /// Render the kept output, with an explicit marker naming how much was dropped.
        /// </summary>
        /// <returns>Head, omission marker when anything was dropped, and tail.</returns>
        public string Render()
        {
            lock (_Lock)
            {
                StringBuilder rendered = new StringBuilder(_Head.ToString());
                if (_OmittedBytes > 0)
                    rendered.Append("\n[... ").Append(_OmittedBytes).Append(" bytes of output omitted; the beginning and the end are kept ...]\n\n");
                foreach (string line in _Tail) rendered.Append(line);
                return rendered.ToString();
            }
        }

        #endregion

        #region Private-Methods

        private static string TakeLastBytes(string text, int maximumBytes)
        {
            byte[] encoded = Encoding.UTF8.GetBytes(text);
            if (encoded.Length <= maximumBytes) return text;

            // Start on a character boundary: skip UTF-8 continuation bytes (10xxxxxx).
            int start = encoded.Length - maximumBytes;
            while (start < encoded.Length && (encoded[start] & 0xC0) == 0x80) start++;
            return Encoding.UTF8.GetString(encoded, start, encoded.Length - start);
        }

        #endregion
    }
}
