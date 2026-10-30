namespace Armada.Core.Database
{
    using Armada.Core.Enums;

    /// <summary>
    /// Statements that drop the <c>coordination_rooms</c>, <c>coordination_messages</c>,
    /// <c>coordination_participants</c> and <c>coordination_claims</c> tables together with their indexes.
    /// Nothing reads or writes those tables. The <c>coordination_leases</c> table is a separate distributed
    /// lock store and is never touched here. Every provider migration uses these definitions, and a database
    /// that never created the tables ends the same, so an upgraded database matches a fresh install.
    /// </summary>
    internal static class CoordinationBoardDropSchema
    {
        #region Private-Members

        private static readonly string[] _Tables = new string[]
        {
            "coordination_claims",
            "coordination_participants",
            "coordination_messages",
            "coordination_rooms"
        };

        #endregion

        // Static initializers run in textual order, so the table list is declared before the statement arrays that read it.

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

        // Dropping a table drops its indexes on every provider, so one statement per table removes both.
        private static string[] Build(DatabaseTypeEnum provider)
        {
            string[] statements = new string[_Tables.Length];
            for (int i = 0; i < _Tables.Length; i++)
            {
                statements[i] = provider == DatabaseTypeEnum.SqlServer
                    ? "IF OBJECT_ID('" + _Tables[i] + "', 'U') IS NOT NULL DROP TABLE " + _Tables[i] + ";"
                    : "DROP TABLE IF EXISTS " + _Tables[i] + ";";
            }

            return statements;
        }

        #endregion
    }
}
