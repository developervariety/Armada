namespace Armada.Core.Database.Mysql.Queries
{
    internal static class VesselPreviewSchema
    {
        /// <summary>Append-only vessel preview configuration.</summary>
        internal static readonly string[] MigrationV75Statements = new string[]
        {
            @"ALTER TABLE vessels ADD COLUMN require_passing_checks_to_land INTEGER NOT NULL DEFAULT 0;",
            @"ALTER TABLE vessels ADD COLUMN protected_branch_patterns LONGTEXT NOT NULL DEFAULT ('[]');",
            @"ALTER TABLE vessels ADD COLUMN release_branch_prefix LONGTEXT NOT NULL DEFAULT ('release/');",
            @"ALTER TABLE vessels ADD COLUMN hotfix_branch_prefix LONGTEXT NOT NULL DEFAULT ('hotfix/');",
            @"ALTER TABLE vessels ADD COLUMN require_pull_request_for_protected_branches INTEGER NOT NULL DEFAULT 0;",
            @"ALTER TABLE vessels ADD COLUMN require_merge_queue_for_release_branches INTEGER NOT NULL DEFAULT 0;"
        };

    }
}
