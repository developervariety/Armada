namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// Records the byte size of every module written into a generated captain instruction file, so the
    /// admiral can report what it actually sent instead of asking the captain to estimate it. Captain
    /// self-reports are approximate by nature: a 2026-07-30 diagnostic captain could only describe its
    /// tool definitions as "about 42,000 visible characters" and reported no provider total at all.
    /// Sizes are counted in UTF-8 bytes so they match a wc -c taken against the written file.
    /// </summary>
    public sealed class PromptModuleLedger
    {
        #region Public-Members

        /// <summary>
        /// Module name to UTF-8 byte count, in the order the modules were tracked. A module tracked more
        /// than once accumulates, which is itself a duplication signal worth reading in the telemetry.
        /// </summary>
        public IReadOnlyDictionary<string, int> Modules
        {
            get { return _Modules; }
        }

        /// <summary>
        /// Total UTF-8 bytes across every tracked module.
        /// </summary>
        public int TotalBytes
        {
            get { return _TotalBytes; }
        }

        #endregion

        #region Private-Members

        private readonly Dictionary<string, int> _Modules = new Dictionary<string, int>(StringComparer.Ordinal);
        private int _TotalBytes = 0;

        #endregion

        #region Public-Methods

        /// <summary>
        /// Records the size of one module and returns the text unchanged, so a call site can wrap an
        /// existing append without restructuring it.
        /// </summary>
        /// <param name="name">Stable module name, for example "mission.rules".</param>
        /// <param name="text">Module text about to be appended; null or empty is tracked as zero.</param>
        /// <returns>The supplied text, unchanged.</returns>
        public string Track(string name, string? text)
        {
            if (String.IsNullOrEmpty(name)) throw new ArgumentNullException(nameof(name));

            int bytes = String.IsNullOrEmpty(text) ? 0 : System.Text.Encoding.UTF8.GetByteCount(text);

            if (_Modules.ContainsKey(name)) _Modules[name] = _Modules[name] + bytes;
            else _Modules[name] = bytes;

            _TotalBytes = _TotalBytes + bytes;

            return text ?? "";
        }

        /// <summary>
        /// Returns the tracked modules ordered largest first, which is the order an operator wants when
        /// asking where a brief's bytes went.
        /// </summary>
        /// <returns>Module names and byte counts, largest first.</returns>
        public List<KeyValuePair<string, int>> GetModulesLargestFirst()
        {
            List<KeyValuePair<string, int>> ordered = new List<KeyValuePair<string, int>>(_Modules);
            ordered.Sort(_CompareByBytesDescending);
            return ordered;
        }

        #endregion

        #region Private-Methods

        private static int _CompareByBytesDescending(KeyValuePair<string, int> left, KeyValuePair<string, int> right)
        {
            int byBytes = right.Value.CompareTo(left.Value);
            if (byBytes != 0) return byBytes;
            return String.CompareOrdinal(left.Key, right.Key);
        }

        #endregion
    }
}
