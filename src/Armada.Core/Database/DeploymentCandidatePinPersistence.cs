namespace Armada.Core.Database
{
    /// <summary>
    /// Provider-specific schema changes that pin a deployment to one candidate: the resolved source commit, the
    /// artifact identity, the commit and artifact the approver approved, the required-evidence outcome, and the
    /// environment's list of Check types required at the candidate commit. Every column is nullable, so an
    /// existing row reads as an unpinned deployment and an existing environment requires no evidence.
    /// </summary>
    internal static class DeploymentCandidatePinPersistence
    {
        internal static readonly string[] SqliteStatements =
        {
            @"ALTER TABLE deployments ADD COLUMN source_commit TEXT NULL;",
            @"ALTER TABLE deployments ADD COLUMN artifact_identity TEXT NULL;",
            @"ALTER TABLE deployments ADD COLUMN approved_source_commit TEXT NULL;",
            @"ALTER TABLE deployments ADD COLUMN approved_artifact_identity TEXT NULL;",
            @"ALTER TABLE deployments ADD COLUMN validation_check_run_ids_json TEXT NULL;",
            @"ALTER TABLE deployments ADD COLUMN validation_summary TEXT NULL;",
            @"ALTER TABLE environments ADD COLUMN required_check_types_json TEXT NULL;"
        };

        internal static readonly string[] PostgresqlStatements =
        {
            @"ALTER TABLE deployments ADD COLUMN IF NOT EXISTS source_commit TEXT NULL;",
            @"ALTER TABLE deployments ADD COLUMN IF NOT EXISTS artifact_identity TEXT NULL;",
            @"ALTER TABLE deployments ADD COLUMN IF NOT EXISTS approved_source_commit TEXT NULL;",
            @"ALTER TABLE deployments ADD COLUMN IF NOT EXISTS approved_artifact_identity TEXT NULL;",
            @"ALTER TABLE deployments ADD COLUMN IF NOT EXISTS validation_check_run_ids_json TEXT NULL;",
            @"ALTER TABLE deployments ADD COLUMN IF NOT EXISTS validation_summary TEXT NULL;",
            @"ALTER TABLE environments ADD COLUMN IF NOT EXISTS required_check_types_json TEXT NULL;"
        };

        internal static readonly string[] MysqlStatements =
        {
            @"ALTER TABLE deployments ADD COLUMN source_commit LONGTEXT NULL;",
            @"ALTER TABLE deployments ADD COLUMN artifact_identity LONGTEXT NULL;",
            @"ALTER TABLE deployments ADD COLUMN approved_source_commit LONGTEXT NULL;",
            @"ALTER TABLE deployments ADD COLUMN approved_artifact_identity LONGTEXT NULL;",
            @"ALTER TABLE deployments ADD COLUMN validation_check_run_ids_json LONGTEXT NULL;",
            @"ALTER TABLE deployments ADD COLUMN validation_summary LONGTEXT NULL;",
            @"ALTER TABLE environments ADD COLUMN required_check_types_json LONGTEXT NULL;"
        };

        internal static readonly string[] SqlServerStatements =
        {
            @"IF COL_LENGTH('deployments', 'source_commit') IS NULL ALTER TABLE deployments ADD source_commit NVARCHAR(MAX) NULL;",
            @"IF COL_LENGTH('deployments', 'artifact_identity') IS NULL ALTER TABLE deployments ADD artifact_identity NVARCHAR(MAX) NULL;",
            @"IF COL_LENGTH('deployments', 'approved_source_commit') IS NULL ALTER TABLE deployments ADD approved_source_commit NVARCHAR(MAX) NULL;",
            @"IF COL_LENGTH('deployments', 'approved_artifact_identity') IS NULL ALTER TABLE deployments ADD approved_artifact_identity NVARCHAR(MAX) NULL;",
            @"IF COL_LENGTH('deployments', 'validation_check_run_ids_json') IS NULL ALTER TABLE deployments ADD validation_check_run_ids_json NVARCHAR(MAX) NULL;",
            @"IF COL_LENGTH('deployments', 'validation_summary') IS NULL ALTER TABLE deployments ADD validation_summary NVARCHAR(MAX) NULL;",
            @"IF COL_LENGTH('environments', 'required_check_types_json') IS NULL ALTER TABLE environments ADD required_check_types_json NVARCHAR(MAX) NULL;"
        };
    }
}
