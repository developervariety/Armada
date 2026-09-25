namespace Armada.Core.Database
{
    using System;
    using System.Collections.Generic;
    using System.Data;
    using System.Linq;

    /// <summary>
    /// One table as the shared method sets see it: its name, its columns, the columns an update leaves alone, and the
    /// reader and writer that map a row to its model. The insert and update statements are the same text on every provider.
    /// </summary>
    /// <typeparam name="TModel">Model a row maps to.</typeparam>
    internal sealed class StoredTable<TModel> where TModel : class
    {
        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="name">Table name.</param>
        /// <param name="columns">Every column the writer binds, in insert order. The key column is "id".</param>
        /// <param name="unchangedByUpdate">Columns other than "id" that an update leaves as they were created.</param>
        /// <param name="read">Maps a row to its model.</param>
        /// <param name="write">Binds every column of a model, each as "@" plus its column.</param>
        internal StoredTable(
            string name,
            IReadOnlyList<string> columns,
            IReadOnlyList<string> unchangedByUpdate,
            Func<IDataRecord, StoredValueConverter, TModel> read,
            Action<StoredParameters, TModel> write)
        {
            if (String.IsNullOrWhiteSpace(name)) throw new ArgumentNullException(nameof(name));
            if (columns == null || columns.Count == 0) throw new ArgumentNullException(nameof(columns));
            if (!columns.Contains("id")) throw new ArgumentException("A stored table is keyed by its id column.", nameof(columns));
            foreach (string column in unchangedByUpdate ?? throw new ArgumentNullException(nameof(unchangedByUpdate)))
                if (!columns.Contains(column)) throw new ArgumentException("Unknown column " + column + " in " + name + ".", nameof(unchangedByUpdate));

            Name = name;
            Columns = columns;
            Read = read ?? throw new ArgumentNullException(nameof(read));
            Write = write ?? throw new ArgumentNullException(nameof(write));
            InsertSql = "INSERT INTO " + name + " (" + String.Join(", ", columns) + ") VALUES ("
                + String.Join(", ", columns.Select(c => "@" + c)) + ");";
            UpdateSql = "UPDATE " + name + " SET "
                + String.Join(", ", columns.Where(c => c != "id" && !unchangedByUpdate.Contains(c)).Select(c => c + " = @" + c))
                + " WHERE id = @id;";
        }

        /// <summary>
        /// Table name.
        /// </summary>
        internal string Name { get; }

        /// <summary>
        /// Every column the writer binds, in insert order.
        /// </summary>
        internal IReadOnlyList<string> Columns { get; }

        /// <summary>
        /// Maps a row to its model.
        /// </summary>
        internal Func<IDataRecord, StoredValueConverter, TModel> Read { get; }

        /// <summary>
        /// Binds every column of a model.
        /// </summary>
        internal Action<StoredParameters, TModel> Write { get; }

        /// <summary>
        /// Inserts one row with every column.
        /// </summary>
        internal string InsertSql { get; }

        /// <summary>
        /// Rewrites every changeable column of the row with a given id.
        /// </summary>
        internal string UpdateSql { get; }

        /// <summary>
        /// A filter over this table.
        /// </summary>
        internal StoredFilter Filter() => new StoredFilter(Name);
    }
}
