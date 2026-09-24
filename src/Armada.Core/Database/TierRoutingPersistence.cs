namespace Armada.Core.Database
{
    using System;
    using System.Data;
    using System.Data.Common;
    using Armada.Core.Models;

    /// <summary>
    /// Persists the routing fields that live on records rather than in settings: a captain's preference rank
    /// within its tier and a persona's minimum routing tier. Every provider binds and reads them through this
    /// class, so the values are stored and restored identically.
    /// </summary>
    internal static class TierRoutingPersistence
    {
        #region Public-Members

        /// <summary>Column holding a captain's preference rank within its tier.</summary>
        internal const string PreferenceRankColumn = "preference_rank";

        internal const string MinimumTierColumn = "minimum_tier";

        /// <summary>SQLite migration statements adding the routing columns.</summary>
        internal static readonly string[] SqliteStatements = new string[]
        {
            @"ALTER TABLE captains ADD COLUMN preference_rank INTEGER NOT NULL DEFAULT 0;",
            @"ALTER TABLE personas ADD COLUMN specialist INTEGER NOT NULL DEFAULT 0;"
        };

        /// <summary>PostgreSQL migration statements adding the routing columns.</summary>
        internal static readonly string[] PostgresqlStatements = new string[]
        {
            @"ALTER TABLE captains ADD COLUMN IF NOT EXISTS preference_rank INTEGER NOT NULL DEFAULT 0;",
            @"ALTER TABLE personas ADD COLUMN IF NOT EXISTS specialist BOOLEAN NOT NULL DEFAULT FALSE;"
        };

        /// <summary>MySQL migration statements adding the routing columns.</summary>
        internal static readonly string[] MysqlStatements = new string[]
        {
            @"ALTER TABLE captains ADD COLUMN preference_rank INT NOT NULL DEFAULT 0;",
            @"ALTER TABLE personas ADD COLUMN specialist TINYINT(1) NOT NULL DEFAULT 0;"
        };

        /// <summary>SQL Server migration statements adding the routing columns.</summary>
        internal static readonly string[] SqlServerStatements = new string[]
        {
            @"IF COL_LENGTH('captains', 'preference_rank') IS NULL ALTER TABLE captains ADD preference_rank INT NOT NULL CONSTRAINT df_captains_preference_rank DEFAULT 0;",
            @"IF COL_LENGTH('personas', 'specialist') IS NULL ALTER TABLE personas ADD specialist BIT NOT NULL CONSTRAINT df_personas_specialist DEFAULT 0;"
        };

        #endregion

        #region Internal-Methods

        /// <summary>Bind the captain preference rank for an insert or update.</summary>
        /// <param name="command">Command to bind.</param>
        /// <param name="captain">Captain whose rank is stored.</param>
        internal static void AddCaptain(DbCommand command, Captain captain)
        {
            DbParameter rank = command.CreateParameter();
            rank.ParameterName = "@" + PreferenceRankColumn;
            rank.DbType = DbType.Int32;
            rank.Value = captain.PreferenceRank;
            command.Parameters.Add(rank);
        }

        /// <summary>Bind persona routing fields for an insert or update.</summary>
        /// <param name="command">Command to bind.</param>
        /// <param name="persona">Persona whose minimum tier is stored.</param>
        internal static void AddPersona(DbCommand command, Persona persona)
        {
            DbParameter minimumTier = command.CreateParameter();
            minimumTier.ParameterName = "@" + MinimumTierColumn;
            minimumTier.DbType = DbType.String;
            minimumTier.Value = persona.MinimumTier.HasValue ? persona.MinimumTier.Value.ToString() : DBNull.Value;
            command.Parameters.Add(minimumTier);
        }

        #endregion
    }
}
