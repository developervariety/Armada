namespace Armada.Core.Database
{
    /// <summary>
    /// Provider-specific schema changes for the workflow profile's declared gate-input paths: the files its
    /// required checks read, stored as a JSON list. The column is nullable, so an existing profile reads as one
    /// that declares none.
    /// </summary>
    internal static class WorkflowProfileGateInputPersistence
    {
        internal static readonly string[] SqliteStatements =
        {
            @"ALTER TABLE workflow_profiles ADD COLUMN gate_input_paths_json TEXT NULL;"
        };

        internal static readonly string[] PostgresqlStatements =
        {
            @"ALTER TABLE workflow_profiles ADD COLUMN IF NOT EXISTS gate_input_paths_json TEXT NULL;"
        };

        internal static readonly string[] MysqlStatements =
        {
            @"ALTER TABLE workflow_profiles ADD COLUMN gate_input_paths_json LONGTEXT NULL;"
        };

        internal static readonly string[] SqlServerStatements =
        {
            @"IF COL_LENGTH('workflow_profiles', 'gate_input_paths_json') IS NULL ALTER TABLE workflow_profiles ADD gate_input_paths_json NVARCHAR(MAX) NULL;"
        };
    }
}
