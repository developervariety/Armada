namespace Armada.Core.Services.Archive
{
    using System;
    using System.Globalization;
    using System.Linq;

    /// <summary>
    /// The object key layout of the history archive. Raw history lives under <c>raw/</c> and is kept
    /// for the raw retention; trace records live under <c>trace/</c> and are kept for the trace
    /// retention. Every key starts with the configured prefix.
    /// <list type="bullet">
    /// <item><c>raw/&lt;category&gt;/&lt;yyyy&gt;/&lt;MM&gt;/&lt;relative path&gt;</c> - one archived file.</item>
    /// <item><c>raw/db/&lt;table&gt;/&lt;yyyy-MM-ddTHHmmssZ&gt;-&lt;run&gt;.jsonl.gz</c> - expired database rows.</item>
    /// <item><c>raw/manifests/&lt;yyyy&gt;/&lt;MM&gt;/&lt;yyyy-MM-ddTHHmmssZ&gt;-&lt;run&gt;.jsonl</c> - one run's manifest.</item>
    /// <item><c>trace/&lt;yyyy&gt;/&lt;MM&gt;/&lt;id&gt;.json</c> - one trace record.</item>
    /// </list>
    /// </summary>
    public static class ArchiveKeys
    {
        #region Public-Members

        /// <summary>
        /// Top-level folder of raw history.
        /// </summary>
        public const string RawFolder = "raw";

        /// <summary>
        /// Top-level folder of trace records.
        /// </summary>
        public const string TraceFolder = "trace";

        #endregion

        #region Public-Methods

        /// <summary>
        /// Key of one archived file.
        /// </summary>
        /// <param name="prefix">Configured key prefix.</param>
        /// <param name="category">History category, for example <c>missionLogs</c>.</param>
        /// <param name="whenUtc">Time that dates the file, normally its last write.</param>
        /// <param name="relativePath">Path of the file below its category root.</param>
        /// <returns>Object key.</returns>
        public static string Raw(string prefix, string category, DateTime whenUtc, string relativePath)
        {
            return Join(prefix, RawFolder + "/" + Segment(category) + "/" + Month(whenUtc) + "/" + RelativeKey(relativePath));
        }

        /// <summary>
        /// Key of one table's expired-row export.
        /// </summary>
        /// <param name="prefix">Configured key prefix.</param>
        /// <param name="table">Table name.</param>
        /// <param name="runUtc">Time of the expiry run.</param>
        /// <param name="runId">Identifier of the run.</param>
        /// <returns>Object key.</returns>
        public static string Database(string prefix, string table, DateTime runUtc, string runId)
        {
            return Join(prefix, RawFolder + "/db/" + Segment(table) + "/" + Stamp(runUtc) + "-" + Segment(runId) + ".jsonl.gz");
        }

        /// <summary>
        /// Key of one run's manifest.
        /// </summary>
        /// <param name="prefix">Configured key prefix.</param>
        /// <param name="runUtc">Time of the run.</param>
        /// <param name="runId">Identifier of the run.</param>
        /// <returns>Object key.</returns>
        public static string Manifest(string prefix, DateTime runUtc, string runId)
        {
            return Join(prefix, RawFolder + "/manifests/" + Month(runUtc) + "/" + Stamp(runUtc) + "-" + Segment(runId) + ".jsonl");
        }

        /// <summary>
        /// Key of one trace record.
        /// </summary>
        /// <param name="prefix">Configured key prefix.</param>
        /// <param name="whenUtc">Time that dates the record, normally the entity's creation.</param>
        /// <param name="id">Entity identifier, for example a mission id.</param>
        /// <returns>Object key.</returns>
        public static string Trace(string prefix, DateTime whenUtc, string id)
        {
            return Join(prefix, TraceFolder + "/" + Month(whenUtc) + "/" + Segment(id) + ".json");
        }

        /// <summary>
        /// Prefix that lists every object of one top-level folder.
        /// </summary>
        /// <param name="prefix">Configured key prefix.</param>
        /// <param name="folder"><see cref="RawFolder"/> or <see cref="TraceFolder"/>.</param>
        /// <returns>Listing prefix ending in a slash.</returns>
        public static string FolderPrefix(string prefix, string folder)
        {
            return Join(prefix, Segment(folder) + "/");
        }

        #endregion

        #region Private-Methods

        private static string Join(string prefix, string rest)
        {
            string trimmed = (prefix ?? String.Empty).Trim().Trim('/');
            return trimmed.Length == 0 ? rest : trimmed + "/" + rest;
        }

        private static string Month(DateTime whenUtc)
        {
            DateTime utc = whenUtc.Kind == DateTimeKind.Utc ? whenUtc : whenUtc.ToUniversalTime();
            return utc.ToString("yyyy", CultureInfo.InvariantCulture) + "/" + utc.ToString("MM", CultureInfo.InvariantCulture);
        }

        private static string Stamp(DateTime whenUtc)
        {
            DateTime utc = whenUtc.Kind == DateTimeKind.Utc ? whenUtc : whenUtc.ToUniversalTime();
            return utc.ToString("yyyy-MM-dd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        }

        private static string Segment(string value)
        {
            if (String.IsNullOrWhiteSpace(value)) throw new ArgumentException("Archive key segment is empty.", nameof(value));
            string cleaned = new string(value.Trim().Select(c => c == '/' || c == '\\' || Char.IsControl(c) ? '_' : c).ToArray());
            if (cleaned == "." || cleaned == "..") throw new ArgumentException("Archive key segment cannot be '" + value + "'.", nameof(value));
            return cleaned;
        }

        private static string RelativeKey(string relativePath)
        {
            if (String.IsNullOrWhiteSpace(relativePath)) throw new ArgumentException("Archived file path is empty.", nameof(relativePath));
            string[] parts = relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) throw new ArgumentException("Archived file path is empty.", nameof(relativePath));
            return String.Join("/", parts.Select(Segment));
        }

        #endregion
    }
}
