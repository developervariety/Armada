namespace Armada.Core.Database
{
    using System;
    using System.Data.Common;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;

    /// <summary>
    /// What one provider needs to run the shared method sets: how to open a connection, how its stored values bind
    /// and read, and the few statement shapes whose syntax differs between providers (first row, any row, paging).
    /// Everything else in a shared statement is the same text on every provider.
    /// </summary>
    internal sealed class StoredDialect
    {
        private readonly Func<DbConnection> _Connect;

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="provider">Provider the statements run on.</param>
        /// <param name="connect">Creates an unopened connection.</param>
        /// <param name="values">How the provider's stored values read.</param>
        internal StoredDialect(DatabaseTypeEnum provider, Func<DbConnection> connect, StoredValueConverter values)
        {
            Provider = provider;
            _Connect = connect ?? throw new ArgumentNullException(nameof(connect));
            Values = values ?? throw new ArgumentNullException(nameof(values));
            Binder = StoredValueBinder.For(provider);
        }

        /// <summary>
        /// Provider the statements run on.
        /// </summary>
        internal DatabaseTypeEnum Provider { get; }

        /// <summary>
        /// How the provider's stored values bind.
        /// </summary>
        internal StoredValueBinder Binder { get; }

        /// <summary>
        /// How the provider's stored values read.
        /// </summary>
        internal StoredValueConverter Values { get; }

        /// <summary>
        /// Open a new connection.
        /// </summary>
        internal async Task<DbConnection> OpenAsync(CancellationToken token)
        {
            DbConnection connection = _Connect();
            try
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                return connection;
            }
            catch
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        /// <summary>
        /// A statement that reads at most the first matching row of a table.
        /// </summary>
        /// <param name="table">Table to read.</param>
        /// <param name="where">Conditions, without the WHERE keyword.</param>
        internal string SelectFirst(string table, string where)
        {
            return Provider == DatabaseTypeEnum.SqlServer
                ? "SELECT TOP 1 * FROM " + table + " WHERE " + where + ";"
                : "SELECT * FROM " + table + " WHERE " + where + " LIMIT 1;";
        }

        /// <summary>
        /// A statement that returns one row when any row of a table matches, and none otherwise.
        /// </summary>
        /// <param name="table">Table to test.</param>
        /// <param name="where">A WHERE clause with a leading space, or an empty string.</param>
        internal string SelectAny(string table, string where)
        {
            return Provider == DatabaseTypeEnum.SqlServer
                ? "SELECT TOP 1 1 FROM " + table + where + ";"
                : "SELECT 1 FROM " + table + where + " LIMIT 1;";
        }

        /// <summary>
        /// The ORDER BY and paging tail of a paged read. The page size binds as @page_size and the row offset as @offset.
        /// </summary>
        /// <param name="order">Order terms, without the ORDER BY keyword.</param>
        internal string OrderAndPage(string order)
        {
            return Provider == DatabaseTypeEnum.SqlServer
                ? " ORDER BY " + order + " OFFSET @offset ROWS FETCH NEXT @page_size ROWS ONLY;"
                : " ORDER BY " + order + " LIMIT @page_size OFFSET @offset;";
        }
    }
}
