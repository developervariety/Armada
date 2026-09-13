namespace Armada.Core.Database.Mysql.Queries
{
    internal static class BackendMetadataSchema
    {
        internal static readonly string[] MigrationV76Statements = new string[]
        {
            @"ALTER TABLE captains ADD COLUMN tier VARCHAR(32) NULL;",
            @"ALTER TABLE missions ADD COLUMN tier VARCHAR(32) NULL;",
            @"ALTER TABLE voyages ADD COLUMN source_planning_session_id VARCHAR(450) CHARACTER SET utf8mb4 NULL;",
            @"ALTER TABLE voyages ADD COLUMN source_planning_message_id VARCHAR(450) CHARACTER SET utf8mb4 NULL;"
        };
    }
}
