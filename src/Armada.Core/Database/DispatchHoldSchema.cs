namespace Armada.Core.Database
{
    /// <summary>
    /// Schema of the dispatch hold table for each supported database. The table holds the engaged fleet-wide hold, at most one row, and one row per held vessel.
    /// </summary>
    internal static class DispatchHoldSchema
    {
        internal static readonly string[] SqliteStatements =
        {
            "CREATE TABLE IF NOT EXISTS dispatch_holds (id TEXT NOT NULL PRIMARY KEY, reason TEXT NOT NULL, set_by TEXT, set_utc TEXT NOT NULL);"
        };

        internal static readonly string[] PostgresqlStatements =
        {
            "CREATE TABLE IF NOT EXISTS dispatch_holds (id TEXT PRIMARY KEY, reason TEXT NOT NULL, set_by TEXT, set_utc TIMESTAMPTZ NOT NULL);"
        };

        internal static readonly string[] MysqlStatements =
        {
            "CREATE TABLE IF NOT EXISTS dispatch_holds (id VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL, reason TEXT NOT NULL, set_by VARCHAR(256) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NULL, set_utc DATETIME(6) NOT NULL, PRIMARY KEY (id));"
        };

        internal static readonly string[] SqlServerStatements =
        {
            "IF OBJECT_ID('dispatch_holds','U') IS NULL CREATE TABLE dispatch_holds (id NVARCHAR(64) NOT NULL PRIMARY KEY, reason NVARCHAR(MAX) NOT NULL, set_by NVARCHAR(256) NULL, set_utc DATETIME2 NOT NULL);"
        };
    }
}
