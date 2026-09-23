namespace Armada.Core.Database
{
    /// <summary>Provider-specific schema changes for persona routing floors.</summary>
    internal static class PersonaMinimumTierPersistence
    {
        internal static readonly string[] SqliteStatements =
        {
            @"ALTER TABLE personas ADD COLUMN minimum_tier TEXT NULL;",
            @"UPDATE personas SET minimum_tier = CASE WHEN lower(replace(name, ' ', '')) = 'testengineer' THEN 'Standard' ELSE 'Premium' END WHERE specialist = 1;"
        };

        internal static readonly string[] PostgresqlStatements =
        {
            @"ALTER TABLE personas ADD COLUMN IF NOT EXISTS minimum_tier TEXT NULL;",
            @"UPDATE personas SET minimum_tier = CASE WHEN lower(replace(name, ' ', '')) = 'testengineer' THEN 'Standard' ELSE 'Premium' END WHERE specialist = TRUE;"
        };

        internal static readonly string[] MysqlStatements =
        {
            @"ALTER TABLE personas ADD COLUMN minimum_tier VARCHAR(32) NULL;",
            @"UPDATE personas SET minimum_tier = CASE WHEN LOWER(REPLACE(name, ' ', '')) = 'testengineer' THEN 'Standard' ELSE 'Premium' END WHERE specialist = 1;"
        };

        internal static readonly string[] SqlServerStatements =
        {
            @"IF COL_LENGTH('personas', 'minimum_tier') IS NULL ALTER TABLE personas ADD minimum_tier NVARCHAR(32) NULL;",
            @"UPDATE personas SET minimum_tier = CASE WHEN LOWER(REPLACE(name, ' ', '')) = 'testengineer' THEN 'Standard' ELSE 'Premium' END WHERE specialist = 1;"
        };
    }
}
