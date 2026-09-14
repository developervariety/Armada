namespace Armada.Core.Models
{
    using System;
    using System.Collections.Generic;
    using System.Linq;

    /// <summary>
    /// Rows deleted by one data expiry run, per table in purge order. A table the run did not purge
    /// because its retention is disabled is absent, so a zero always means the table was examined.
    /// </summary>
    public sealed class DataExpiryResult
    {
        private readonly List<KeyValuePair<string, int>> _Tables = new List<KeyValuePair<string, int>>();

        /// <summary>Deleted row counts per table, in purge order.</summary>
        public IReadOnlyList<KeyValuePair<string, int>> Tables => _Tables;

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
            for (int i = 0; i < _Tables.Count; i++)
            {
                if (String.Equals(_Tables[i].Key, table, StringComparison.Ordinal))
                {
                    _Tables[i] = new KeyValuePair<string, int>(table, _Tables[i].Value + deleted);
                    return;
                }
            }
            _Tables.Add(new KeyValuePair<string, int>(table, deleted));
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

        /// <summary>Render as <c>table=count</c> pairs in purge order.</summary>
        /// <returns>The counts.</returns>
        public override string ToString()
        {
            return _Tables.Count == 0 ? "no tables purged" : String.Join(" ", _Tables.Select(item => item.Key + "=" + item.Value));
        }
    }
}
