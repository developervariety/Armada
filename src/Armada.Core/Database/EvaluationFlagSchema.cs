namespace Armada.Core.Database
{
    /// <summary>
    /// Adds <c>is_evaluation</c> to missions and voyages. The column is not indexed: no list
    /// filters on it. Production summaries exclude the rows in memory.
    /// </summary>
    internal static class EvaluationFlagSchema
    {
        /// <summary>Column name on missions and voyages.</summary>
        internal const string Column = "is_evaluation";

        /// <summary>SQLite statements.</summary>
        internal static readonly string[] SqliteStatements = new string[]
        {
            "ALTER TABLE missions ADD COLUMN is_evaluation INTEGER NOT NULL DEFAULT 0;",
            "ALTER TABLE voyages ADD COLUMN is_evaluation INTEGER NOT NULL DEFAULT 0;"
        };

        /// <summary>PostgreSQL statements.</summary>
        internal static readonly string[] PostgresqlStatements = new string[]
        {
            "ALTER TABLE missions ADD COLUMN IF NOT EXISTS is_evaluation BOOLEAN NOT NULL DEFAULT FALSE;",
            "ALTER TABLE voyages ADD COLUMN IF NOT EXISTS is_evaluation BOOLEAN NOT NULL DEFAULT FALSE;"
        };

        /// <summary>MySQL statements.</summary>
        internal static readonly string[] MysqlStatements = new string[]
        {
            "ALTER TABLE missions ADD COLUMN is_evaluation TINYINT(1) NOT NULL DEFAULT 0;",
            "ALTER TABLE voyages ADD COLUMN is_evaluation TINYINT(1) NOT NULL DEFAULT 0;"
        };

        /// <summary>SQL Server statements.</summary>
        internal static readonly string[] SqlServerStatements = new string[]
        {
            "IF COL_LENGTH('missions', 'is_evaluation') IS NULL ALTER TABLE missions ADD is_evaluation BIT NOT NULL CONSTRAINT df_missions_is_evaluation DEFAULT 0;",
            "IF COL_LENGTH('voyages', 'is_evaluation') IS NULL ALTER TABLE voyages ADD is_evaluation BIT NOT NULL CONSTRAINT df_voyages_is_evaluation DEFAULT 0;"
        };
    }
}
