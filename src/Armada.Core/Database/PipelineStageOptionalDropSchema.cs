namespace Armada.Core.Database
{
    /// <summary>
    /// Provider-specific schema changes that drop the pipeline stage optional flag. No dispatch, handoff or
    /// rescue read it, so a stage marked optional still ran as a mandatory stage; a stage an operator does not
    /// want is removed from the pipeline or skipped with a recorded stage skip.
    /// </summary>
    internal static class PipelineStageOptionalDropSchema
    {
        internal static readonly string[] SqliteStatements =
        {
            @"ALTER TABLE pipeline_stages DROP COLUMN is_optional;"
        };

        internal static readonly string[] PostgresqlStatements =
        {
            @"ALTER TABLE pipeline_stages DROP COLUMN IF EXISTS is_optional;"
        };

        internal static readonly string[] MysqlStatements =
        {
            @"ALTER TABLE pipeline_stages DROP COLUMN is_optional;"
        };

        // SQL Server names the column's default constraint itself, and a column cannot be dropped while its
        // default constraint exists, so the constraint is found by catalog and dropped first.
        internal static readonly string[] SqlServerStatements =
        {
            @"DECLARE @constraint sysname;
SELECT @constraint = dc.name
FROM sys.default_constraints dc
JOIN sys.columns c ON c.default_object_id = dc.object_id
WHERE dc.parent_object_id = OBJECT_ID('pipeline_stages') AND c.name = 'is_optional';
IF @constraint IS NOT NULL EXEC('ALTER TABLE pipeline_stages DROP CONSTRAINT ' + QUOTENAME(@constraint));
IF COL_LENGTH('pipeline_stages', 'is_optional') IS NOT NULL EXEC('ALTER TABLE pipeline_stages DROP COLUMN is_optional');"
        };
    }
}
