namespace Armada.Core.Database
{
    /// <summary>
    /// Provider-specific schema changes for the earlier run a check took its result from. The column is nullable, so
    /// an existing check reads as one that ran its own command.
    /// </summary>
    internal static class CheckRunSatisfiedByPersistence
    {
        internal static readonly string[] SqliteStatements =
        {
            @"ALTER TABLE check_runs ADD COLUMN satisfied_by_json TEXT NULL;"
        };

        internal static readonly string[] PostgresqlStatements =
        {
            @"ALTER TABLE check_runs ADD COLUMN IF NOT EXISTS satisfied_by_json TEXT NULL;"
        };

        internal static readonly string[] MysqlStatements =
        {
            @"ALTER TABLE check_runs ADD COLUMN satisfied_by_json LONGTEXT NULL;"
        };

        internal static readonly string[] SqlServerStatements =
        {
            @"IF COL_LENGTH('check_runs', 'satisfied_by_json') IS NULL ALTER TABLE check_runs ADD satisfied_by_json NVARCHAR(MAX) NULL;"
        };
    }
}
