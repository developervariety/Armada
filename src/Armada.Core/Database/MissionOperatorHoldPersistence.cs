namespace Armada.Core.Database
{
    using System;
    using System.Data;
    using System.Data.Common;
    using Armada.Core.Models;

    /// <summary>
    /// Persists the Judge PASS operator-review hold on a mission row. Every provider binds and reads the
    /// hold through this class, so the flag and its reason are stored and restored identically.
    /// </summary>
    internal static class MissionOperatorHoldPersistence
    {
        #region Public-Members

        /// <summary>Column holding whether the mission's Judge PASS is held for operator review.</summary>
        internal const string HeldColumn = "held_for_operator_review";

        /// <summary>Column holding why the Judge PASS is held.</summary>
        internal const string ReasonColumn = "held_for_operator_review_reason";

        /// <summary>SQLite migration statements adding the hold columns.</summary>
        internal static readonly string[] SqliteStatements = new string[]
        {
            @"ALTER TABLE missions ADD COLUMN held_for_operator_review INTEGER NOT NULL DEFAULT 0;",
            @"ALTER TABLE missions ADD COLUMN held_for_operator_review_reason TEXT NULL;"
        };

        /// <summary>PostgreSQL migration statements adding the hold columns.</summary>
        internal static readonly string[] PostgresqlStatements = new string[]
        {
            @"ALTER TABLE missions ADD COLUMN IF NOT EXISTS held_for_operator_review BOOLEAN NOT NULL DEFAULT FALSE;",
            @"ALTER TABLE missions ADD COLUMN IF NOT EXISTS held_for_operator_review_reason TEXT NULL;"
        };

        /// <summary>MySQL migration statements adding the hold columns.</summary>
        internal static readonly string[] MysqlStatements = new string[]
        {
            @"ALTER TABLE missions ADD COLUMN held_for_operator_review TINYINT(1) NOT NULL DEFAULT 0;",
            @"ALTER TABLE missions ADD COLUMN held_for_operator_review_reason LONGTEXT CHARACTER SET utf8mb4 NULL;"
        };

        /// <summary>SQL Server migration statements adding the hold columns.</summary>
        internal static readonly string[] SqlServerStatements = new string[]
        {
            @"IF COL_LENGTH('missions', 'held_for_operator_review') IS NULL ALTER TABLE missions ADD held_for_operator_review BIT NOT NULL CONSTRAINT df_missions_held_for_operator_review DEFAULT 0;",
            @"IF COL_LENGTH('missions', 'held_for_operator_review_reason') IS NULL ALTER TABLE missions ADD held_for_operator_review_reason NVARCHAR(MAX) NULL;"
        };

        #endregion

        #region Internal-Methods

        /// <summary>Bind the hold parameters for an insert or update.</summary>
        /// <param name="command">Command to bind.</param>
        /// <param name="mission">Mission whose hold is stored.</param>
        internal static void Add(DbCommand command, Mission mission)
        {
            DbParameter held = command.CreateParameter();
            held.ParameterName = "@" + HeldColumn;
            held.DbType = DbType.Boolean;
            held.Value = mission.HeldForOperatorReview;
            command.Parameters.Add(held);

            DbParameter reason = command.CreateParameter();
            reason.ParameterName = "@" + ReasonColumn;
            reason.DbType = DbType.String;
            reason.Value = mission.HeldForOperatorReview && !String.IsNullOrWhiteSpace(mission.HeldForOperatorReviewReason)
                ? mission.HeldForOperatorReviewReason!
                : DBNull.Value;
            command.Parameters.Add(reason);
        }

        #endregion
    }
}
