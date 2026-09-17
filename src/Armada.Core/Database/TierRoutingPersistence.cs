namespace Armada.Core.Database
{
    using System;
    using System.Data;
    using System.Data.Common;
    using Armada.Core.Models;

    /// <summary>
    /// Persists the routing fields that live on records rather than in settings: a captain's preference rank
    /// within its tier and a persona's specialist flag. Every provider binds and reads them through this
    /// class, so the values are stored and restored identically.
    /// </summary>
    internal static class TierRoutingPersistence
    {
        #region Public-Members

        /// <summary>Column holding a captain's preference rank within its tier.</summary>
        internal const string PreferenceRankColumn = "preference_rank";

        /// <summary>Column holding whether a persona requires the Premium tier.</summary>
        internal const string SpecialistColumn = "specialist";

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

        /// <summary>Read the captain preference rank from a captain row.</summary>
        /// <param name="reader">Reader positioned on a captain row that selects the rank column.</param>
        /// <param name="captain">Captain to populate.</param>
        internal static void ReadCaptain(DbDataReader reader, Captain captain)
        {
            object rank = reader[PreferenceRankColumn];
            captain.PreferenceRank = rank == DBNull.Value ? 0 : Convert.ToInt32(rank);
        }

        /// <summary>Bind the persona specialist flag for an insert or update.</summary>
        /// <param name="command">Command to bind.</param>
        /// <param name="persona">Persona whose flag is stored.</param>
        internal static void AddPersona(DbCommand command, Persona persona)
        {
            DbParameter specialist = command.CreateParameter();
            specialist.ParameterName = "@" + SpecialistColumn;
            specialist.DbType = DbType.Boolean;
            specialist.Value = persona.Specialist;
            command.Parameters.Add(specialist);
        }

        /// <summary>Read the persona specialist flag from a persona row.</summary>
        /// <param name="reader">Reader positioned on a persona row that selects the flag column.</param>
        /// <param name="persona">Persona to populate.</param>
        internal static void ReadPersona(DbDataReader reader, Persona persona)
        {
            object specialist = reader[SpecialistColumn];
            persona.Specialist = specialist != DBNull.Value && Convert.ToBoolean(specialist);
        }

        #endregion
    }
}
