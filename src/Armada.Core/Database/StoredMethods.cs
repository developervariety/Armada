namespace Armada.Core.Database
{
    using System;
    using System.Collections.Generic;
    using System.Data.Common;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;

    /// <summary>
    /// The plain reads and writes every method set performs over one table on one provider: insert, update by id,
    /// read the first match, list, page, count and delete by filter. A method set composes these; a write that must
    /// compare and set, touch another table or run in a transaction stays in the method set itself.
    /// </summary>
    /// <typeparam name="TModel">Model a row maps to.</typeparam>
    internal sealed class StoredMethods<TModel> where TModel : class
    {
        /// <summary>
        /// Page size a paged read uses when the caller asks for none.
        /// </summary>
        internal const int DefaultPageSize = 25;

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="dialect">Provider the statements run on.</param>
        /// <param name="table">Table the statements address.</param>
        internal StoredMethods(StoredDialect dialect, StoredTable<TModel> table)
        {
            Dialect = dialect ?? throw new ArgumentNullException(nameof(dialect));
            Table = table ?? throw new ArgumentNullException(nameof(table));
        }

        /// <summary>
        /// Provider the statements run on.
        /// </summary>
        internal StoredDialect Dialect { get; }

        /// <summary>
        /// Table the statements address.
        /// </summary>
        internal StoredTable<TModel> Table { get; }

        /// <summary>
        /// A filter over the table.
        /// </summary>
        internal StoredFilter Filter() => Table.Filter();

        /// <summary>
        /// Insert one row with every column.
        /// </summary>
        internal async Task InsertAsync(TModel model, CancellationToken token)
        {
            if (model == null) throw new ArgumentNullException(nameof(model));
            using (DbConnection connection = await Dialect.OpenAsync(token).ConfigureAwait(false))
            using (DbCommand command = connection.CreateCommand())
            {
                command.CommandText = Table.InsertSql;
                Table.Write(Dialect.Binder.For(command, Table.Name), model);
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Insert one row with every column inside a transaction the caller owns, for a write that spans tables.
        /// </summary>
        internal async Task InsertAsync(TModel model, DbConnection connection, DbTransaction transaction, CancellationToken token)
        {
            if (model == null) throw new ArgumentNullException(nameof(model));
            if (connection == null) throw new ArgumentNullException(nameof(connection));
            if (transaction == null) throw new ArgumentNullException(nameof(transaction));
            using (DbCommand command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = Table.InsertSql;
                Table.Write(Dialect.Binder.For(command, Table.Name), model);
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Rewrite every changeable column of the row with the model's key.
        /// </summary>
        /// <returns>Rows changed.</returns>
        internal async Task<int> UpdateAsync(TModel model, CancellationToken token)
        {
            if (model == null) throw new ArgumentNullException(nameof(model));
            using (DbConnection connection = await Dialect.OpenAsync(token).ConfigureAwait(false))
            using (DbCommand command = connection.CreateCommand())
            {
                command.CommandText = Table.UpdateSql;
                Table.Write(Dialect.Binder.For(command, Table.Name), model);
                return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Read the first row that matches a filter with at least one condition.
        /// </summary>
        internal async Task<TModel?> FirstAsync(StoredFilter filter, CancellationToken token)
        {
            RequireConditions(filter);
            using (DbConnection connection = await Dialect.OpenAsync(token).ConfigureAwait(false))
            using (DbCommand command = connection.CreateCommand())
            {
                command.CommandText = Dialect.SelectFirst(Table.Name, filter.Conjunction);
                filter.Bind(command, Dialect.Binder);
                using (DbDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
                {
                    if (await reader.ReadAsync(token).ConfigureAwait(false)) return Table.Read(reader, Dialect.Values);
                    return null;
                }
            }
        }

        /// <summary>
        /// Read every row that matches a filter, in an order.
        /// </summary>
        /// <param name="filter">Conditions; none reads every row.</param>
        /// <param name="order">Order terms, without the ORDER BY keyword.</param>
        /// <param name="token">Cancellation token.</param>
        internal async Task<List<TModel>> ListAsync(StoredFilter filter, string order, CancellationToken token)
        {
            RequireSameTable(filter);
            using (DbConnection connection = await Dialect.OpenAsync(token).ConfigureAwait(false))
            using (DbCommand command = connection.CreateCommand())
            {
                command.CommandText = "SELECT * FROM " + Table.Name + filter.Where + " ORDER BY " + order + ";";
                filter.Bind(command, Dialect.Binder);
                return await ReadAllAsync(command, token).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Whether any row matches a filter.
        /// </summary>
        /// <param name="filter">Conditions; none tests for any row at all.</param>
        /// <param name="token">Cancellation token.</param>
        internal async Task<bool> AnyAsync(StoredFilter filter, CancellationToken token)
        {
            RequireSameTable(filter);
            using (DbConnection connection = await Dialect.OpenAsync(token).ConfigureAwait(false))
            using (DbCommand command = connection.CreateCommand())
            {
                command.CommandText = Dialect.SelectAny(Table.Name, filter.Where);
                filter.Bind(command, Dialect.Binder);
                object? result = await command.ExecuteScalarAsync(token).ConfigureAwait(false);
                return result != null && result != DBNull.Value;
            }
        }

        /// <summary>
        /// Count the rows that match a filter.
        /// </summary>
        internal async Task<long> CountAsync(StoredFilter filter, CancellationToken token)
        {
            RequireSameTable(filter);
            using (DbConnection connection = await Dialect.OpenAsync(token).ConfigureAwait(false))
            {
                return await CountAsync(connection, filter, token).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Read one page of the rows that match a filter, with the count of every match. A page size of zero or less
        /// reads <see cref="DefaultPageSize"/> rows; a page number of one or less reads the first page.
        /// </summary>
        /// <param name="filter">Conditions; none pages every row.</param>
        /// <param name="order">Order terms, without the ORDER BY keyword.</param>
        /// <param name="pageNumber">Page number, from 1.</param>
        /// <param name="pageSize">Rows per page.</param>
        /// <param name="token">Cancellation token.</param>
        internal async Task<EnumerationResult<TModel>> PageAsync(StoredFilter filter, string order, int pageNumber, int pageSize, CancellationToken token)
        {
            RequireSameTable(filter);
            int size = pageSize <= 0 ? DefaultPageSize : pageSize;
            int offset = pageNumber <= 1 ? 0 : (pageNumber - 1) * size;

            using (DbConnection connection = await Dialect.OpenAsync(token).ConfigureAwait(false))
            {
                long total = await CountAsync(connection, filter, token).ConfigureAwait(false);
                List<TModel> rows;
                using (DbCommand command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT * FROM " + Table.Name + filter.Where + Dialect.OrderAndPage(order);
                    filter.Bind(command, Dialect.Binder);
                    StoredValueBinder.Value(command, "@page_size", size);
                    StoredValueBinder.Value(command, "@offset", offset);
                    rows = await ReadAllAsync(command, token).ConfigureAwait(false);
                }

                return new EnumerationResult<TModel>
                {
                    PageNumber = pageNumber,
                    PageSize = size,
                    TotalRecords = total,
                    TotalPages = (int)Math.Ceiling((double)total / size),
                    Objects = rows
                };
            }
        }

        /// <summary>
        /// Delete the rows that match a filter.
        /// </summary>
        /// <param name="filter">Conditions; none deletes every row.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Rows deleted.</returns>
        internal async Task<int> DeleteAsync(StoredFilter filter, CancellationToken token)
        {
            RequireSameTable(filter);
            using (DbConnection connection = await Dialect.OpenAsync(token).ConfigureAwait(false))
            using (DbCommand command = connection.CreateCommand())
            {
                command.CommandText = "DELETE FROM " + Table.Name + filter.Where + ";";
                filter.Bind(command, Dialect.Binder);
                return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
        }

        private async Task<long> CountAsync(DbConnection connection, StoredFilter filter, CancellationToken token)
        {
            using (DbCommand command = connection.CreateCommand())
            {
                command.CommandText = "SELECT COUNT(*) FROM " + Table.Name + filter.Where + ";";
                filter.Bind(command, Dialect.Binder);
                return Convert.ToInt64(await command.ExecuteScalarAsync(token).ConfigureAwait(false));
            }
        }

        private async Task<List<TModel>> ReadAllAsync(DbCommand command, CancellationToken token)
        {
            List<TModel> rows = new List<TModel>();
            using (DbDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(token).ConfigureAwait(false)) rows.Add(Table.Read(reader, Dialect.Values));
            }
            return rows;
        }

        private void RequireSameTable(StoredFilter filter)
        {
            if (filter == null) throw new ArgumentNullException(nameof(filter));
            if (!String.Equals(filter.Table, Table.Name, StringComparison.Ordinal))
                throw new ArgumentException("A filter over " + filter.Table + " cannot address " + Table.Name + ".", nameof(filter));
        }

        private void RequireConditions(StoredFilter filter)
        {
            RequireSameTable(filter);
            if (filter.Conditions.Count == 0)
                throw new ArgumentException("A single-row read of " + Table.Name + " needs at least one condition.", nameof(filter));
        }
    }
}
