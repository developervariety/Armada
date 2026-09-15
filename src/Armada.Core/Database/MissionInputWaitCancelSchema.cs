namespace Armada.Core.Database
{
    using System;
    using System.Collections.Generic;
    using Armada.Core.Enums;

    /// <summary>
    /// Statements that cancel every mission stored with the <c>WaitingForInput</c> status, a value the mission status
    /// set does not define. The row keeps its last update time so time-windowed sweeps do not read it as a new change,
    /// takes that time as its completion time when it has none, and records why it was cancelled ahead of any earlier
    /// failure reason. Every provider migration uses these definitions, so all providers change exactly the same rows.
    /// </summary>
    internal static class MissionInputWaitCancelSchema
    {
        #region Public-Members

        /// <summary>Failure reason written on each cancelled mission.</summary>
        internal const string CancelReason = "Cancelled while waiting for operator input";

        /// <summary>SQLite statements.</summary>
        internal static readonly string[] SqliteStatements = Build(DatabaseTypeEnum.Sqlite);

        /// <summary>PostgreSQL statements.</summary>
        internal static readonly string[] PostgresqlStatements = Build(DatabaseTypeEnum.Postgresql);

        /// <summary>MySQL statements.</summary>
        internal static readonly string[] MysqlStatements = Build(DatabaseTypeEnum.Mysql);

        /// <summary>SQL Server statements.</summary>
        internal static readonly string[] SqlServerStatements = Build(DatabaseTypeEnum.SqlServer);

        #endregion

        #region Private-Members

        private const string _StoredStatus = "'WaitingForInput'";

        #endregion

        #region Private-Methods

        private static string[] Build(DatabaseTypeEnum provider)
        {
            List<string> statements = new List<string>();

            statements.Add("UPDATE missions SET failure_reason = CASE WHEN failure_reason IS NULL OR failure_reason = '' THEN '"
                + CancelReason + "' ELSE " + Concat(provider, "'" + CancelReason + "; previous reason: '", "failure_reason") + " END, "
                + "completed_utc = COALESCE(completed_utc, last_update_utc) WHERE status = " + _StoredStatus + ";");

            statements.Add("UPDATE missions SET status = 'Cancelled' WHERE status = " + _StoredStatus + ";");

            return statements.ToArray();
        }

        private static string Concat(DatabaseTypeEnum provider, string left, string right)
        {
            switch (provider)
            {
                case DatabaseTypeEnum.Sqlite:
                case DatabaseTypeEnum.Postgresql:
                    return left + " || " + right;
                case DatabaseTypeEnum.Mysql:
                    return "CONCAT(" + left + ", " + right + ")";
                case DatabaseTypeEnum.SqlServer:
                    return left + " + " + right;
                default:
                    throw new NotSupportedException("Unsupported database provider: " + provider);
            }
        }

        #endregion
    }
}
