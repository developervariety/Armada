#nullable enable

namespace Armada.Test.Database
{
    using System;
    using System.Collections.Generic;
    using System.Data.Common;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Settings;
    using Npgsql;

    /// <summary>
    /// The PostgreSQL plans of the hot filtered reads under the binder's parameter types.
    /// </summary>
    internal sealed class StoredBinderSchemaTests
    {
        private readonly DatabaseSettings _Settings;

        internal StoredBinderSchemaTests(DatabaseSettings settings)
        {
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        /// <summary>
        /// The hot filtered reads plan the same index whether the filter is bound by driver inference or by the
        /// binder. The plans are printed so a run records them.
        /// </summary>
        internal async Task VerifyPostgresqlHotReadPlansAsync(CancellationToken token)
        {
            if (_Settings.Type != DatabaseTypeEnum.Postgresql) throw new DatabaseTestSkipException("postgresql_only");
            HotRead[] reads = new[]
            {
                new HotRead("missions by status", "missions", "SELECT * FROM missions WHERE status = @status ORDER BY priority ASC, created_utc ASC;", "status", "InProgress", null),
                new HotRead("voyages by status", "voyages", "SELECT * FROM voyages WHERE status = @status ORDER BY created_utc DESC;", "status", "Open", null),
                new HotRead("events by type", "events", "SELECT * FROM events WHERE event_type = @event_type ORDER BY created_utc DESC LIMIT @limit;", "event_type", "mission.completed", 100)
            };
            List<string> problems = new List<string>();
            using (NpgsqlConnection connection = new NpgsqlConnection(_Settings.GetConnectionString()))
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (NpgsqlCommand off = new NpgsqlCommand("SET enable_seqscan = off;", connection))
                    await off.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                foreach (HotRead read in reads)
                {
                    string inferred = await ExplainAsync(connection, read, false, token).ConfigureAwait(false);
                    string bound = await ExplainAsync(connection, read, true, token).ConfigureAwait(false);
                    Console.WriteLine("PLAN " + read.Label + " (driver inference): " + inferred);
                    Console.WriteLine("PLAN " + read.Label + " (binder): " + bound);
                    string inferredIndex = IndexOf(inferred);
                    string boundIndex = IndexOf(bound);
                    if (inferredIndex.Length == 0) problems.Add(read.Label + " uses no index: " + inferred);
                    if (!String.Equals(inferredIndex, boundIndex, StringComparison.Ordinal))
                        problems.Add(read.Label + " plans " + inferredIndex + " by inference but " + boundIndex + " through the binder");
                }
            }

            DatabaseAssert.True(problems.Count == 0, String.Join("; ", problems));
        }

        private static async Task<string> ExplainAsync(NpgsqlConnection connection, HotRead read, bool throughBinder, CancellationToken token)
        {
            using (NpgsqlCommand command = new NpgsqlCommand("EXPLAIN " + read.Sql, connection))
            {
                if (throughBinder)
                {
                    StoredParameters parameters = PostgresqlBinder().For(command, read.Table).Text(read.Column, read.Value);
                    if (read.Limit.HasValue) parameters.Int("@limit", "limit", read.Limit.Value);
                }
                else
                {
                    command.Parameters.AddWithValue("@" + read.Column, read.Value);
                    if (read.Limit.HasValue) command.Parameters.AddWithValue("@limit", read.Limit.Value);
                }

                List<string> lines = new List<string>();
                using (DbDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
                {
                    while (await reader.ReadAsync(token).ConfigureAwait(false)) lines.Add(reader.GetString(0).Trim());
                }

                return String.Join(" | ", lines);
            }
        }

        private static string IndexOf(string plan)
        {
            const string marker = " using ";
            int start = plan.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (start < 0) return String.Empty;
            start += marker.Length;
            int end = plan.IndexOf(' ', start);
            return end < 0 ? plan.Substring(start) : plan.Substring(start, end - start);
        }

        private static StoredValueBinder PostgresqlBinder()
        {
            return Armada.Core.Database.Postgresql.PostgresqlDatabaseDriver.StoredBinder;
        }

        private sealed class HotRead
        {
            internal string Label { get; }
            internal string Table { get; }
            internal string Sql { get; }
            internal string Column { get; }
            internal string Value { get; }
            internal int? Limit { get; }

            internal HotRead(string label, string table, string sql, string column, string value, int? limit)
            {
                Label = label;
                Table = table;
                Sql = sql;
                Column = column;
                Value = value;
                Limit = limit;
            }
        }
    }
}
