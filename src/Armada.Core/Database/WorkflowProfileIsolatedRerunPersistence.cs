namespace Armada.Core.Database
{
    /// <summary>
    /// Provider-specific schema changes for the workflow profile's isolated re-run fields: the line pattern
    /// that reads failing test names from the unit-test output, and the command that re-runs those tests alone.
    /// </summary>
    internal static class WorkflowProfileIsolatedRerunPersistence
    {
        internal static readonly string[] SqliteStatements =
        {
            @"ALTER TABLE workflow_profiles ADD COLUMN failed_test_line_pattern TEXT NULL;",
            @"ALTER TABLE workflow_profiles ADD COLUMN isolated_test_rerun_command TEXT NULL;"
        };

        internal static readonly string[] PostgresqlStatements =
        {
            @"ALTER TABLE workflow_profiles ADD COLUMN IF NOT EXISTS failed_test_line_pattern TEXT NULL;",
            @"ALTER TABLE workflow_profiles ADD COLUMN IF NOT EXISTS isolated_test_rerun_command TEXT NULL;"
        };

        internal static readonly string[] MysqlStatements =
        {
            @"ALTER TABLE workflow_profiles ADD COLUMN failed_test_line_pattern LONGTEXT NULL;",
            @"ALTER TABLE workflow_profiles ADD COLUMN isolated_test_rerun_command LONGTEXT NULL;"
        };

        internal static readonly string[] SqlServerStatements =
        {
            @"IF COL_LENGTH('workflow_profiles', 'failed_test_line_pattern') IS NULL ALTER TABLE workflow_profiles ADD failed_test_line_pattern NVARCHAR(MAX) NULL;",
            @"IF COL_LENGTH('workflow_profiles', 'isolated_test_rerun_command') IS NULL ALTER TABLE workflow_profiles ADD isolated_test_rerun_command NVARCHAR(MAX) NULL;"
        };
    }
}
