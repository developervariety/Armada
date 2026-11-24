namespace Armada.Core.Database
{
    /// <summary>Nullable opt-in permission and its independent conditional-write revision.</summary>
    internal static class MissionAutomaticWorkPermissionSchema
    {
        internal static readonly string[] SqliteStatements = new string[]
        {
            "ALTER TABLE missions ADD COLUMN automatic_work_permission_json TEXT NULL;",
            "ALTER TABLE missions ADD COLUMN automatic_work_permission_revision INTEGER NOT NULL DEFAULT 0;"
        };

        internal static readonly string[] PostgresqlStatements = new string[]
        {
            "ALTER TABLE missions ADD COLUMN IF NOT EXISTS automatic_work_permission_json TEXT NULL;",
            "ALTER TABLE missions ADD COLUMN IF NOT EXISTS automatic_work_permission_revision BIGINT NOT NULL DEFAULT 0;"
        };

        internal static readonly string[] MysqlStatements = new string[]
        {
            "ALTER TABLE missions ADD COLUMN automatic_work_permission_json LONGTEXT NULL;",
            "ALTER TABLE missions ADD COLUMN automatic_work_permission_revision BIGINT NOT NULL DEFAULT 0;"
        };

        internal static readonly string[] SqlServerStatements = new string[]
        {
            "IF COL_LENGTH('missions', 'automatic_work_permission_json') IS NULL ALTER TABLE missions ADD automatic_work_permission_json NVARCHAR(MAX) NULL;",
            "IF COL_LENGTH('missions', 'automatic_work_permission_revision') IS NULL ALTER TABLE missions ADD automatic_work_permission_revision BIGINT NOT NULL DEFAULT 0;"
        };
    }
}
