namespace Armada.Core.Database
{
    using Armada.Core.Enums;

    /// <summary>
    /// Statements that drop the <c>jobs</c> table and its <c>idx_jobs_created</c> index. Nothing reads or writes
    /// that table: long-running Admiral jobs are journalled under the data directory, and Harbor and landing jobs
    /// have their own tables, which these statements never touch. Every provider migration uses these definitions,
    /// so an upgraded database ends with the same schema as a fresh install.
    /// </summary>
    internal static class JobsTableDropSchema
    {
        #region Public-Members

        /// <summary>SQLite statements.</summary>
        internal static readonly string[] SqliteStatements = Build(DatabaseTypeEnum.Sqlite);

        /// <summary>PostgreSQL statements.</summary>
        internal static readonly string[] PostgresqlStatements = Build(DatabaseTypeEnum.Postgresql);

        /// <summary>MySQL statements.</summary>
        internal static readonly string[] MysqlStatements = Build(DatabaseTypeEnum.Mysql);

        /// <summary>SQL Server statements.</summary>
        internal static readonly string[] SqlServerStatements = Build(DatabaseTypeEnum.SqlServer);

        #endregion

        #region Private-Methods

        // Dropping a table drops its indexes on every provider, so one statement removes both.
        private static string[] Build(DatabaseTypeEnum provider)
        {
            return new string[]
            {
                provider == DatabaseTypeEnum.SqlServer
                    ? "IF OBJECT_ID('jobs', 'U') IS NOT NULL DROP TABLE jobs;"
                    : "DROP TABLE IF EXISTS jobs;"
            };
        }

        #endregion
    }
}
