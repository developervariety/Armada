namespace Armada.Runtimes
{
    using System;
    using System.Collections.Generic;
    using System.Text;

    /// <summary>
    /// Joins streamed text pieces into whole lines. A runtime that streams assistant text in pieces can split a
    /// protocol marker across two events ("[ARMADA:RES" then "ULT] COMPLETE"), and a marker only counts when it
    /// starts a physical line of one record. The runtime appends each piece, writes the complete lines it gets
    /// back as records, and flushes the unfinished line when the stream ends (the terminal event or process exit).
    /// One instance belongs to one runtime launch.
    /// </summary>
    internal sealed class StreamingTextLineAssembler
    {
        #region Private-Members

        private readonly object _Lock = new object();
        private readonly StringBuilder _Pending = new StringBuilder();

        #endregion

        #region Public-Methods

        /// <summary>
        /// Append one streamed piece of text.
        /// </summary>
        /// <param name="piece">Streamed text; may hold no, one or several line breaks.</param>
        /// <returns>Every line the piece completed, without its line break, in order.</returns>
        public List<string> Append(string? piece)
        {
            List<string> lines = new List<string>();
            if (String.IsNullOrEmpty(piece)) return lines;

            lock (_Lock)
            {
                _Pending.Append(piece);
                string text = _Pending.ToString();
                int start = 0;
                int newline;
                while ((newline = text.IndexOf('\n', start)) >= 0)
                {
                    lines.Add(text.Substring(start, newline - start).TrimEnd('\r'));
                    start = newline + 1;
                }

                _Pending.Clear();
                _Pending.Append(text, start, text.Length - start);
            }

            return lines;
        }

        /// <summary>
        /// Take the unfinished line, if any, and clear it.
        /// </summary>
        /// <returns>The unfinished line, or an empty string.</returns>
        public string Flush()
        {
            lock (_Lock)
            {
                string rest = _Pending.ToString();
                _Pending.Clear();
                return rest.TrimEnd('\r');
            }
        }

        #endregion
    }
}
