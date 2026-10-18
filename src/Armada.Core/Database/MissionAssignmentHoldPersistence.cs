namespace Armada.Core.Database
{
    /// <summary>
    /// Provider-specific schema changes for the assignment hold on a mission row: the reason code the assignment pass
    /// records while it holds a Pending mission, and the record that holds it.
    /// </summary>
    internal static class MissionAssignmentHoldPersistence
    {
        /// <summary>Column holding the assignment hold reason code.</summary>
        internal const string ReasonColumn = "assignment_hold_reason";

        /// <summary>Column holding the identifier of the record that holds the mission.</summary>
        internal const string EntityColumn = "assignment_hold_entity_id";

        internal static readonly string[] SqliteStatements =
        {
            @"ALTER TABLE missions ADD COLUMN assignment_hold_reason TEXT NULL;",
            @"ALTER TABLE missions ADD COLUMN assignment_hold_entity_id TEXT NULL;"
        };

        internal static readonly string[] PostgresqlStatements =
        {
            @"ALTER TABLE missions ADD COLUMN IF NOT EXISTS assignment_hold_reason TEXT NULL;",
            @"ALTER TABLE missions ADD COLUMN IF NOT EXISTS assignment_hold_entity_id TEXT NULL;"
        };

        internal static readonly string[] MysqlStatements =
        {
            @"ALTER TABLE missions ADD COLUMN assignment_hold_reason VARCHAR(128) NULL;",
            @"ALTER TABLE missions ADD COLUMN assignment_hold_entity_id VARCHAR(450) NULL;"
        };

        internal static readonly string[] SqlServerStatements =
        {
            @"IF COL_LENGTH('missions', 'assignment_hold_reason') IS NULL ALTER TABLE missions ADD assignment_hold_reason NVARCHAR(128) NULL;",
            @"IF COL_LENGTH('missions', 'assignment_hold_entity_id') IS NULL ALTER TABLE missions ADD assignment_hold_entity_id NVARCHAR(450) NULL;"
        };
    }
}
