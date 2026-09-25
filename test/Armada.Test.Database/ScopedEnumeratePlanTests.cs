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
    /// The PostgreSQL plans of the hot scoped page reads. Each read is planned as its statement text is written with
    /// its filter values bound, and the plans are printed so a run records them.
    /// </summary>
    internal sealed class ScopedEnumeratePlanTests
    {
        private readonly DatabaseSettings _Settings;

        internal ScopedEnumeratePlanTests(DatabaseSettings settings)
        {
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        /// <summary>
        /// Every hot scoped page read plans an index on its scope column.
        /// </summary>
        internal async Task VerifyAsync(CancellationToken token)
        {
            if (_Settings.Type != DatabaseTypeEnum.Postgresql) throw new DatabaseTestSkipException("postgresql_only");
            DateTime to = DateTime.UtcNow;
            DateTime from = to.AddDays(-1);
            PlannedRead[] reads = new[]
            {
                new PlannedRead("check runs by tenant", "check_runs",
                    "SELECT * FROM check_runs WHERE tenant_id = @tenant_id ORDER BY created_utc DESC, id DESC LIMIT @page_size OFFSET @offset;")
                    .Text("@tenant_id", "ten_plan").Page(),
                new PlannedRead("check runs by vessel", "check_runs",
                    "SELECT * FROM check_runs WHERE vessel_id = @vessel_id ORDER BY created_utc DESC, id DESC LIMIT @page_size OFFSET @offset;")
                    .Text("@vessel_id", "vsl_plan").Page(),
                new PlannedRead("deployments by tenant", "deployments",
                    "SELECT * FROM deployments WHERE tenant_id = @tenant_id ORDER BY COALESCE(completed_utc, started_utc, last_update_utc) DESC, created_utc DESC LIMIT @page_size OFFSET @offset;")
                    .Text("@tenant_id", "ten_plan").Page(),
                new PlannedRead("releases by vessel", "releases",
                    "SELECT * FROM releases WHERE vessel_id = @vessel_id ORDER BY COALESCE(published_utc, last_update_utc) DESC, created_utc DESC LIMIT @page_size OFFSET @offset;")
                    .Text("@vessel_id", "vsl_plan").Page(),
                new PlannedRead("token usage by tenant and window", "token_usage",
                    "SELECT * FROM token_usage WHERE tenant_id = @tenant_id AND created_utc >= @from_utc AND created_utc <= @to_utc ORDER BY created_utc DESC LIMIT 25 OFFSET 0;")
                    .Text("@tenant_id", "ten_plan").Time("@from_utc", "created_utc", from).Time("@to_utc", "created_utc", to),
                new PlannedRead("request history by tenant and window", "request_history",
                    "SELECT * FROM request_history WHERE tenant_id = @tenant_id AND created_utc >= @from_utc AND created_utc <= @to_utc ORDER BY created_utc DESC LIMIT 25 OFFSET 0;")
                    .Text("@tenant_id", "ten_plan").Time("@from_utc", "created_utc", from).Time("@to_utc", "created_utc", to)
            };

            List<string> problems = new List<string>();
            using (NpgsqlConnection connection = new NpgsqlConnection(_Settings.GetConnectionString()))
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (NpgsqlCommand off = new NpgsqlCommand("SET enable_seqscan = off;", connection))
                    await off.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                foreach (PlannedRead read in reads)
                {
                    string plan = await ExplainAsync(connection, read, token).ConfigureAwait(false);
                    Console.WriteLine("PLAN " + read.Label + ": " + plan);
                    if (plan.IndexOf(" using idx_", StringComparison.OrdinalIgnoreCase) < 0) problems.Add(read.Label + " uses no index: " + plan);
                }
            }

            DatabaseAssert.True(problems.Count == 0, String.Join("; ", problems));
        }

        private static async Task<string> ExplainAsync(NpgsqlConnection connection, PlannedRead read, CancellationToken token)
        {
            using (NpgsqlCommand command = new NpgsqlCommand("EXPLAIN " + read.Sql, connection))
            {
                read.Bind(command);
                List<string> lines = new List<string>();
                using (DbDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
                {
                    while (await reader.ReadAsync(token).ConfigureAwait(false)) lines.Add(reader.GetString(0).Trim());
                }

                return String.Join(" | ", lines);
            }
        }

        /// <summary>
        /// One planned read: its statement text and the filter values it binds in their stored forms.
        /// </summary>
        private sealed class PlannedRead
        {
            private readonly List<Action<NpgsqlCommand>> _Binds = new List<Action<NpgsqlCommand>>();

            internal PlannedRead(string label, string table, string sql)
            {
                Label = label;
                Table = table;
                Sql = sql;
            }

            internal string Label { get; }

            internal string Table { get; }

            internal string Sql { get; }

            internal PlannedRead Text(string name, string value)
            {
                _Binds.Add(command => StoredValueBinder.Value(command, name, value));
                return this;
            }

            internal PlannedRead Time(string name, string column, DateTime value)
            {
                _Binds.Add(command => Binder().AddTimestamp(command, name, Table, column, value));
                return this;
            }

            internal PlannedRead Page()
            {
                _Binds.Add(command =>
                {
                    StoredValueBinder.Value(command, "@page_size", 25);
                    StoredValueBinder.Value(command, "@offset", 0);
                });
                return this;
            }

            internal void Bind(NpgsqlCommand command)
            {
                foreach (Action<NpgsqlCommand> bind in _Binds) bind(command);
            }

            private static StoredValueBinder Binder() => Armada.Core.Database.Postgresql.PostgresqlDatabaseDriver.StoredBinder;
        }
    }
}
