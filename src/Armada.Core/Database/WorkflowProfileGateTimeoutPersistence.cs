namespace Armada.Core.Database
{
    /// <summary>
    /// Provider-specific schema changes for the workflow profile's definition-of-done command time limit. The column
    /// is nullable, so an existing profile reads as one that uses the global limit.
    /// </summary>
    internal static class WorkflowProfileGateTimeoutPersistence
    {
        internal static readonly string[] SqliteStatements =
        {
            @"ALTER TABLE workflow_profiles ADD COLUMN gate_command_timeout_seconds INTEGER NULL;"
        };

        internal static readonly string[] PostgresqlStatements =
        {
            @"ALTER TABLE workflow_profiles ADD COLUMN IF NOT EXISTS gate_command_timeout_seconds INTEGER NULL;"
        };

        internal static readonly string[] MysqlStatements =
        {
            @"ALTER TABLE workflow_profiles ADD COLUMN gate_command_timeout_seconds INT NULL;"
        };

        internal static readonly string[] SqlServerStatements =
        {
            @"IF COL_LENGTH('workflow_profiles', 'gate_command_timeout_seconds') IS NULL ALTER TABLE workflow_profiles ADD gate_command_timeout_seconds INT NULL;"
        };
    }
}
