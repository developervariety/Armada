namespace Armada.Core.Database
{
    /// <summary>
    /// Provider-specific schema changes for the submitted position of each pipeline stage. Stages that share an
    /// order are parallel siblings, and the position keeps them in the order they were submitted; without it a
    /// provider returns tied rows in whatever order its index or heap yields them.
    /// </summary>
    internal static class PipelineStagePositionPersistence
    {
        internal static readonly string[] SqliteStatements =
        {
            @"ALTER TABLE pipeline_stages ADD COLUMN stage_position INTEGER NOT NULL DEFAULT 0;"
        };

        internal static readonly string[] PostgresqlStatements =
        {
            @"ALTER TABLE pipeline_stages ADD COLUMN IF NOT EXISTS stage_position INTEGER NOT NULL DEFAULT 0;"
        };

        internal static readonly string[] MysqlStatements =
        {
            @"ALTER TABLE pipeline_stages ADD COLUMN stage_position INT NOT NULL DEFAULT 0;"
        };

        internal static readonly string[] SqlServerStatements =
        {
            @"IF COL_LENGTH('pipeline_stages', 'stage_position') IS NULL ALTER TABLE pipeline_stages ADD stage_position INT NOT NULL DEFAULT 0;"
        };
    }
}
