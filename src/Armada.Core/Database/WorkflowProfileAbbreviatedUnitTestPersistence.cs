namespace Armada.Core.Database
{
    /// <summary>
    /// Adds the optional abbreviated unit-test command on a workflow profile. The column is nullable.
    /// An existing profile reads as one that has no abbreviated command. No gate selects the command.
    /// </summary>
    internal static class WorkflowProfileAbbreviatedUnitTestPersistence
    {
        internal static readonly string[] SqliteStatements =
        {
            @"ALTER TABLE workflow_profiles ADD COLUMN abbreviated_unit_test_command TEXT NULL;"
        };

        internal static readonly string[] PostgresqlStatements =
        {
            @"ALTER TABLE workflow_profiles ADD COLUMN IF NOT EXISTS abbreviated_unit_test_command TEXT NULL;"
        };

        internal static readonly string[] MysqlStatements =
        {
            @"ALTER TABLE workflow_profiles ADD COLUMN abbreviated_unit_test_command LONGTEXT NULL;"
        };

        internal static readonly string[] SqlServerStatements =
        {
            @"IF COL_LENGTH('workflow_profiles', 'abbreviated_unit_test_command') IS NULL ALTER TABLE workflow_profiles ADD abbreviated_unit_test_command NVARCHAR(MAX) NULL;"
        };
    }
}
