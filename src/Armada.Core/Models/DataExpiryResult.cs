namespace Armada.Core.Models
{
    using System;
    using System.Collections.Generic;
    using System.Linq;

    /// <summary>
    /// Rows deleted by one data expiry run, per table in purge order, and rows older than the cutoff
    /// that a retention rule kept, per kept class. A table the run did not purge because its
    /// retention is disabled is absent, so a zero always means the table was examined.
    /// </summary>
    public sealed class DataExpiryResult
    {
        private readonly List<KeyValuePair<string, int>> _Tables = new List<KeyValuePair<string, int>>();
        private readonly List<KeyValuePair<string, int>> _Kept = new List<KeyValuePair<string, int>>();

        /// <summary>Deleted row counts per table, in purge order.</summary>
        public IReadOnlyList<KeyValuePair<string, int>> Tables => _Tables;

        /// <summary>Rows older than the cutoff that a retention rule kept, per kept class, in purge order.</summary>
        public IReadOnlyList<KeyValuePair<string, int>> KeptClasses => _Kept;

        /// <summary>Total rows deleted.</summary>
        public int Total => _Tables.Sum(item => item.Value);

        /// <summary>
        /// Add deleted rows to a table's count, keeping the order in which tables were first purged.
        /// </summary>
        /// <param name="table">Table name.</param>
        /// <param name="deleted">Rows deleted.</param>
        public void Add(string table, int deleted)
        {
            if (String.IsNullOrWhiteSpace(table)) throw new ArgumentNullException(nameof(table));
            if (deleted < 0) throw new ArgumentOutOfRangeException(nameof(deleted));
            Accumulate(_Tables, table, deleted);
        }

        /// <summary>
        /// Add rows older than the cutoff that a retention rule kept to a kept class's count, keeping
        /// the order in which classes were first counted.
        /// </summary>
        /// <param name="keptClass">Kept class name, for example <c>incident_latest</c>.</param>
        /// <param name="kept">Rows kept.</param>
        public void AddKept(string keptClass, int kept)
        {
            if (String.IsNullOrWhiteSpace(keptClass)) throw new ArgumentNullException(nameof(keptClass));
            if (kept < 0) throw new ArgumentOutOfRangeException(nameof(kept));
            Accumulate(_Kept, keptClass, kept);
        }

        /// <summary>
        /// Deleted rows for one table, or null when the run did not purge it.
        /// </summary>
        /// <param name="table">Table name.</param>
        /// <returns>The count, or null.</returns>
        public int? Deleted(string table)
        {
            foreach (KeyValuePair<string, int> item in _Tables)
            {
                if (String.Equals(item.Key, table, StringComparison.Ordinal)) return item.Value;
            }
            return null;
        }

        /// <summary>
        /// Rows a retention rule kept for one kept class, or null when the run did not count it.
        /// </summary>
        /// <param name="keptClass">Kept class name.</param>
        /// <returns>The count, or null.</returns>
        public int? Kept(string keptClass)
        {
            foreach (KeyValuePair<string, int> item in _Kept)
            {
                if (String.Equals(item.Key, keptClass, StringComparison.Ordinal)) return item.Value;
            }
            return null;
        }

        /// <summary>
        /// Render as <c>table=count</c> pairs in purge order, followed by <c>kept_class=count</c> pairs.
        /// </summary>
        /// <returns>The counts.</returns>
        public override string ToString()
        {
            if (_Tables.Count == 0) return "no tables purged";
            string deleted = String.Join(" ", _Tables.Select(item => item.Key + "=" + item.Value));
            if (_Kept.Count == 0) return deleted;
            return deleted + " " + String.Join(" ", _Kept.Select(item => "kept_" + item.Key + "=" + item.Value));
        }

        private static void Accumulate(List<KeyValuePair<string, int>> counts, string key, int value)
        {
            for (int i = 0; i < counts.Count; i++)
            {
                if (String.Equals(counts[i].Key, key, StringComparison.Ordinal))
                {
                    counts[i] = new KeyValuePair<string, int>(key, counts[i].Value + value);
                    return;
                }
            }
            counts.Add(new KeyValuePair<string, int>(key, value));
        }
    }
}
