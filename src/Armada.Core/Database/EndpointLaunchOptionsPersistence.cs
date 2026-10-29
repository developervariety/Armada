namespace Armada.Core.Database
{
    /// <summary>
    /// Adds the launch-options document on a model endpoint. The column is nullable. A null document
    /// reads as the default launch options.
    /// </summary>
    internal static class EndpointLaunchOptionsPersistence
    {
        internal static readonly string[] SqliteStatements =
        {
            @"ALTER TABLE model_endpoints ADD COLUMN launch_options_json TEXT NULL;"
        };

        internal static readonly string[] PostgresqlStatements =
        {
            @"ALTER TABLE model_endpoints ADD COLUMN IF NOT EXISTS launch_options_json TEXT NULL;"
        };

        internal static readonly string[] MysqlStatements =
        {
            @"ALTER TABLE model_endpoints ADD COLUMN launch_options_json LONGTEXT NULL;"
        };

        internal static readonly string[] SqlServerStatements =
        {
            @"IF COL_LENGTH('model_endpoints', 'launch_options_json') IS NULL ALTER TABLE model_endpoints ADD launch_options_json NVARCHAR(MAX) NULL;"
        };
    }
}
