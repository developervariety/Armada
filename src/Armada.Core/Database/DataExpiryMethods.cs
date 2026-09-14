namespace Armada.Core.Database
{
    using System;
    using System.Collections.Generic;
    using System.Data.Common;
    using System.Globalization;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;

    /// <summary>
    /// Provider-neutral retention purge. The retention rules are written once here; the only
    /// per-provider facts are how each table stores its timestamps and how the <c>read</c> column is
    /// quoted.
    /// </summary>
    public sealed class DataExpiryMethods : IDataExpiryMethods
    {
        #region Public-Members

        /// <summary>Voyage statuses whose completed voyages, and all their missions, expire.</summary>
        public static readonly IReadOnlyList<VoyageStatusEnum> ExpiringVoyageStatuses = new[] { VoyageStatusEnum.Complete, VoyageStatusEnum.Cancelled };

        /// <summary>Statuses of missions without a voyage that expire once completed.</summary>
        public static readonly IReadOnlyList<MissionStatusEnum> ExpiringStandaloneMissionStatuses = new[] { MissionStatusEnum.Complete, MissionStatusEnum.Failed, MissionStatusEnum.Cancelled };

        /// <summary>Merge entry statuses that expire once completed.</summary>
        public static readonly IReadOnlyList<MergeStatusEnum> ExpiringMergeStatuses = new[] { MergeStatusEnum.Landed, MergeStatusEnum.Cancelled, MergeStatusEnum.Failed };

        #endregion

        #region Private-Members

        private const string _Iso8601Format = "yyyy-MM-ddTHH:mm:ss.fffffffZ";
        private readonly Func<DbConnection> _ConnectionFactory;
        private readonly DatabaseTypeEnum _Provider;

        #endregion

        #region Constructors-and-Factories

        /// <summary>Instantiate.</summary>
        /// <param name="connectionFactory">Creates an unopened provider connection.</param>
        /// <param name="provider">Database provider.</param>
        public DataExpiryMethods(Func<DbConnection> connectionFactory, DatabaseTypeEnum provider)
        {
            _ConnectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
            _Provider = provider;
        }

        #endregion

        #region Public-Methods

        /// <inheritdoc />
        public async Task<DataExpiryResult> PurgeExpiredAsync(DataExpiryCutoffs cutoffs, CancellationToken token = default)
        {
            if (cutoffs == null) throw new ArgumentNullException(nameof(cutoffs));
            DataExpiryResult result = new DataExpiryResult();
            if (!cutoffs.RecordCutoffUtc.HasValue) return result;

            DateTime cutoff = cutoffs.RecordCutoffUtc.Value;
            using (DbConnection connection = _ConnectionFactory())
            {
                await connection.OpenAsync(token).ConfigureAwait(false);

                string expiredVoyageIds = "SELECT id FROM voyages WHERE status IN (" + Literals(ExpiringVoyageStatuses) + ")"
                    + " AND completed_utc IS NOT NULL AND completed_utc < @cutoff";
                string missionsOfExpiredVoyages = "voyage_id IN (" + expiredVoyageIds + ")";
                string expiredStandaloneMissions = "voyage_id IS NULL AND status IN (" + Literals(ExpiringStandaloneMissionStatuses) + ")"
                    + " AND completed_utc IS NOT NULL AND completed_utc < @cutoff";

                // A mission whose parent expires loses the link instead of blocking the delete, matching
                // ON DELETE SET NULL on the providers whose foreign key declares it.
                await ExecuteAsync(connection, "missions", null, ClearParentSql(missionsOfExpiredVoyages), "voyages", cutoff, cutoffs, token).ConfigureAwait(false);
                await ExecuteAsync(connection, "missions", result, "DELETE FROM missions WHERE " + missionsOfExpiredVoyages + ";", "voyages", cutoff, cutoffs, token).ConfigureAwait(false);
                await ExecuteAsync(connection, "voyages", result, "DELETE FROM voyages WHERE status IN (" + Literals(ExpiringVoyageStatuses) + ")"
                    + " AND completed_utc IS NOT NULL AND completed_utc < @cutoff;", "voyages", cutoff, cutoffs, token).ConfigureAwait(false);
                await ExecuteAsync(connection, "missions", null, ClearParentSql(expiredStandaloneMissions), "missions", cutoff, cutoffs, token).ConfigureAwait(false);
                await ExecuteAsync(connection, "missions", result, "DELETE FROM missions WHERE " + expiredStandaloneMissions + ";", "missions", cutoff, cutoffs, token).ConfigureAwait(false);

                await ExecuteAsync(connection, "signals", result, "DELETE FROM signals WHERE " + ReadColumn() + " = @true AND created_utc < @cutoff;",
                    "signals", cutoff, cutoffs, token).ConfigureAwait(false);

                // Objective dispatch attempt records inside the reconciliation look-back are kept whatever
                // the retention period, so an attempt whose process stopped before closing it stays visible.
                await ExecuteAsync(connection, "events", result, "DELETE FROM events WHERE created_utc < @cutoff"
                    + " AND NOT (COALESCE(entity_type, '') = @attempt_entity_type AND created_utc >= @attempt_cutoff);",
                    "events", cutoff, cutoffs, token).ConfigureAwait(false);

                await ExecuteAsync(connection, "docks", result, "DELETE FROM docks WHERE active = @false AND captain_id IS NULL AND created_utc < @cutoff;",
                    "docks", cutoff, cutoffs, token).ConfigureAwait(false);

                await ExecuteAsync(connection, "merge_entries", result, "DELETE FROM merge_entries WHERE status IN (" + Literals(ExpiringMergeStatuses) + ")"
                    + " AND completed_utc IS NOT NULL AND completed_utc < @cutoff;", "merge_entries", cutoff, cutoffs, token).ConfigureAwait(false);
            }
            return result;
        }

        #endregion

        #region Private-Methods

        private static string ClearParentSql(string expiredMissionPredicate)
        {
            // The inner derived table is materialized first, so providers that refuse to read the table
            // they update in a subquery accept the statement.
            return "UPDATE missions SET parent_mission_id = NULL WHERE parent_mission_id IN (SELECT id FROM (SELECT id FROM missions WHERE "
                + expiredMissionPredicate + ") expired_missions);";
        }

        private async Task ExecuteAsync(DbConnection connection, string table, DataExpiryResult? result, string sql, string timestampTable,
            DateTime cutoff, DataExpiryCutoffs cutoffs, CancellationToken token)
        {
            using (DbCommand command = connection.CreateCommand())
            {
                command.CommandText = sql;
                if (sql.Contains("@cutoff", StringComparison.Ordinal)) AddTimestamp(command, "@cutoff", timestampTable, cutoff);
                if (sql.Contains("@attempt_cutoff", StringComparison.Ordinal)) AddTimestamp(command, "@attempt_cutoff", timestampTable, cutoffs.DispatchAttemptCutoffUtc);
                if (sql.Contains("@attempt_entity_type", StringComparison.Ordinal)) ProductionFactSql.Add(command, "@attempt_entity_type", ObjectiveDispatchAdmission.AttemptEntityType);
                if (sql.Contains("@true", StringComparison.Ordinal)) ProductionFactSql.AddBool(command, "@true", true, _Provider);
                if (sql.Contains("@false", StringComparison.Ordinal)) ProductionFactSql.AddBool(command, "@false", false, _Provider);
                int deleted;
                try
                {
                    deleted = await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
                catch (DbException ex)
                {
                    throw new InvalidOperationException("data expiry of " + table + " failed on " + _Provider + ": " + ex.Message, ex);
                }
                result?.Add(table, deleted);
            }
        }

        /// <summary>
        /// Bind a cutoff in the representation the table's timestamp columns use. SQLite stores every
        /// timestamp as ISO-8601 text; SQL Server and PostgreSQL store some operational tables as text
        /// and the rest as native timestamps; MySQL stores native timestamps.
        /// </summary>
        private void AddTimestamp(DbCommand command, string name, string table, DateTime value)
        {
            DateTime utc = value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value.ToUniversalTime(), DateTimeKind.Utc);
            if (StoresTextTimestamps(_Provider, table)) ProductionFactSql.Add(command, name, utc.ToString(_Iso8601Format, CultureInfo.InvariantCulture));
            else ProductionFactSql.AddUtc(command, name, utc, _Provider);
        }

        internal static bool StoresTextTimestamps(DatabaseTypeEnum provider, string table)
        {
            switch (provider)
            {
                case DatabaseTypeEnum.Sqlite:
                    return true;
                case DatabaseTypeEnum.SqlServer:
                    return table == "voyages" || table == "missions" || table == "signals" || table == "events" || table == "docks" || table == "merge_entries";
                case DatabaseTypeEnum.Postgresql:
                    return table == "signals" || table == "events" || table == "merge_entries";
                case DatabaseTypeEnum.Mysql:
                    return false;
                default:
                    throw new NotSupportedException("Data expiry does not support provider " + provider);
            }
        }

        private string ReadColumn()
        {
            return _Provider switch
            {
                DatabaseTypeEnum.Mysql => "`read`",
                DatabaseTypeEnum.SqlServer => "[read]",
                _ => "read"
            };
        }

        private static string Literals<T>(IEnumerable<T> values) where T : struct, Enum
        {
            return String.Join(", ", values.Select(value => "'" + value.ToString() + "'"));
        }

        #endregion
    }
}
