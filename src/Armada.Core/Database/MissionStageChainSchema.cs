namespace Armada.Core.Database
{
    /// <summary>
    /// Adds <c>stage_chain_id</c> to missions. The value is shared by every stage a single mission
    /// description expands into, so the parallel-stage barrier can tell independent root chains apart.
    /// The column is not indexed: it is only read with the other columns of a voyage's missions.
    /// </summary>
    internal static class MissionStageChainSchema
    {
        /// <summary>Column name on missions.</summary>
        internal const string Column = "stage_chain_id";

        /// <summary>SQLite statements.</summary>
        internal static readonly string[] SqliteStatements = new string[]
        {
            "ALTER TABLE missions ADD COLUMN stage_chain_id TEXT;"
        };

        /// <summary>PostgreSQL statements.</summary>
        internal static readonly string[] PostgresqlStatements = new string[]
        {
            "ALTER TABLE missions ADD COLUMN IF NOT EXISTS stage_chain_id TEXT;"
        };

        /// <summary>MySQL statements.</summary>
        internal static readonly string[] MysqlStatements = new string[]
        {
            "ALTER TABLE missions ADD COLUMN stage_chain_id VARCHAR(64);"
        };

        /// <summary>SQL Server statements.</summary>
        internal static readonly string[] SqlServerStatements = new string[]
        {
            "IF COL_LENGTH('missions', 'stage_chain_id') IS NULL ALTER TABLE missions ADD stage_chain_id NVARCHAR(64) NULL;"
        };
    }
}
