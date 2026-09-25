namespace Armada.Test.Database
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Settings;
    using Npgsql;
    using SyslogLogging;

    /// <summary>
    /// A PostgreSQL database whose planning tables predate the planning migration holds their times as text, because
    /// that migration creates the tables only when they are absent. The upgrade converts those text times to
    /// TIMESTAMPTZ and keeps each stored instant; a fresh schema is left as it is.
    /// </summary>
    internal sealed class PlanningTimestampMigrationTests
    {
        private const int _ConversionVersion = 114;
        private const int _CreationVersion = 108;
        private readonly DatabaseSettings _Settings;
        private sealed class StopAfterConversionException : Exception { }

        internal PlanningTimestampMigrationTests(DatabaseSettings settings)
        {
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        internal async Task VerifyAsync(CancellationToken token)
        {
            MigrationScenarioRunner history = new MigrationScenarioRunner(_Settings);
            Dictionary<int, string> installed = await history.ReadHistoryAsync(token).ConfigureAwait(false);
            string sessionId = "pls_text_" + Guid.NewGuid().ToString("N").Substring(0, 12);
            List<object[]> later = new List<object[]>();

            using (NpgsqlConnection conn = new NpgsqlConnection(_Settings.GetConnectionString()))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                await ExecuteAsync(conn, "DROP TABLE IF EXISTS planning_session_messages; DROP TABLE IF EXISTS planning_sessions;", token).ConfigureAwait(false);
                // The tables as they stand before the planning migration: its columns, with the times held as text.
                foreach (string statement in Armada.Core.Database.Postgresql.Queries.TableQueries.GetMigrations().Single(m => m.Version == _CreationVersion).Statements)
                    await ExecuteAsync(conn, statement.Replace("TIMESTAMPTZ", "TEXT"), token).ConfigureAwait(false);
                await ExecuteAsync(conn, "INSERT INTO planning_sessions (id, captain_id, vessel_id, title, status, created_utc, started_utc, completed_utc, last_update_utc) "
                    + "VALUES ('" + sessionId + "', 'cpt_text', 'vsl_text', 'Text times', 'Created', '2027-01-02T03:04:05.1234560Z', '', NULL, '2027-01-02T04:05:06.0000000Z');", token).ConfigureAwait(false);
                await ExecuteAsync(conn, "INSERT INTO planning_session_messages (id, planning_session_id, role, sequence, content, created_utc, last_update_utc) "
                    + "VALUES ('plm_" + sessionId + "', '" + sessionId + "', 'User', 1, 'hello', '2027-01-02T03:04:05.1234560Z', '2027-01-02T03:04:05.1234560Z');", token).ConfigureAwait(false);
                DatabaseAssert.Equal("text", await ColumnTypeAsync(conn, "planning_sessions", "created_utc", token).ConfigureAwait(false), "The upgraded database holds planning times as text");

                using (NpgsqlCommand read = new NpgsqlCommand("SELECT version, description, applied_utc FROM schema_migrations WHERE version > " + _ConversionVersion + " ORDER BY version;", conn))
                using (NpgsqlDataReader reader = await read.ExecuteReaderAsync(token).ConfigureAwait(false))
                {
                    while (await reader.ReadAsync(token).ConfigureAwait(false))
                        later.Add(new object[] { reader.GetValue(0), reader.GetValue(1), reader.GetValue(2) });
                }

                await ExecuteAsync(conn, "DELETE FROM schema_migrations WHERE version >= " + _ConversionVersion + ";", token).ConfigureAwait(false);
            }

            try
            {
                LoggingModule logging = new LoggingModule();
                logging.Settings.EnableConsole = false;
                using (DatabaseDriver upgrade = DatabaseDriverFactory.Create(_Settings, logging))
                {
                    upgrade.MigrationCheckpoint = (version, ordinal) =>
                    {
                        if (version == _ConversionVersion && ordinal == -2) throw new StopAfterConversionException();
                    };
                    try { await upgrade.InitializeAsync(token).ConfigureAwait(false); }
                    catch (StopAfterConversionException) { }
                }

                using (NpgsqlConnection conn = new NpgsqlConnection(_Settings.GetConnectionString()))
                {
                    await conn.OpenAsync(token).ConfigureAwait(false);
                    foreach (string column in new[] { "created_utc", "started_utc", "completed_utc", "last_update_utc" })
                        DatabaseAssert.Equal("timestamp with time zone", await ColumnTypeAsync(conn, "planning_sessions", column, token).ConfigureAwait(false), "planning_sessions." + column + " after the upgrade");
                    foreach (string column in new[] { "created_utc", "last_update_utc" })
                        DatabaseAssert.Equal("timestamp with time zone", await ColumnTypeAsync(conn, "planning_session_messages", column, token).ConfigureAwait(false), "planning_session_messages." + column + " after the upgrade");

                    using (NpgsqlCommand read = new NpgsqlCommand("SELECT created_utc, started_utc, completed_utc, last_update_utc FROM planning_sessions WHERE id = @id;", conn))
                    {
                        read.Parameters.AddWithValue("@id", sessionId);
                        using (NpgsqlDataReader reader = await read.ExecuteReaderAsync(token).ConfigureAwait(false))
                        {
                            DatabaseAssert.True(await reader.ReadAsync(token).ConfigureAwait(false), "The converted session row survives the upgrade");
                            DatabaseAssert.UtcInstant(new DateTime(2027, 1, 2, 3, 4, 5, DateTimeKind.Utc).AddTicks(1234560), DateTime.SpecifyKind(reader.GetDateTime(0), DateTimeKind.Utc), "Converted created_utc");
                            DatabaseAssert.True(reader.IsDBNull(1), "An empty stored start time converts to no start time");
                            DatabaseAssert.True(reader.IsDBNull(2), "A null completion time stays null");
                            DatabaseAssert.UtcInstant(new DateTime(2027, 1, 2, 4, 5, 6, DateTimeKind.Utc), DateTime.SpecifyKind(reader.GetDateTime(3), DateTimeKind.Utc), "Converted last_update_utc");
                        }
                    }
                }
            }
            finally
            {
                using (NpgsqlConnection conn = new NpgsqlConnection(_Settings.GetConnectionString()))
                {
                    await conn.OpenAsync(token).ConfigureAwait(false);
                    foreach (object[] row in later)
                    {
                        using (NpgsqlCommand restore = new NpgsqlCommand("INSERT INTO schema_migrations (version, description, applied_utc) VALUES (@version, @description, @applied_utc) ON CONFLICT (version) DO NOTHING;", conn))
                        {
                            restore.Parameters.AddWithValue("@version", row[0]);
                            restore.Parameters.AddWithValue("@description", row[1]);
                            restore.Parameters.AddWithValue("@applied_utc", row[2]);
                            await restore.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                        }
                    }

                    await ExecuteAsync(conn, "DELETE FROM planning_sessions WHERE id = '" + sessionId + "';", token).ConfigureAwait(false);
                }
            }

            Dictionary<int, string> upgraded = await history.ReadHistoryAsync(token).ConfigureAwait(false);
            DatabaseAssert.Equal(installed.Count, upgraded.Count, "The upgraded ledger holds every installed version");
        }

        private static async Task<string> ColumnTypeAsync(NpgsqlConnection conn, string table, string column, CancellationToken token)
        {
            using (NpgsqlCommand cmd = new NpgsqlCommand("SELECT data_type FROM information_schema.columns WHERE table_schema = current_schema() AND table_name = @table AND column_name = @column;", conn))
            {
                cmd.Parameters.AddWithValue("@table", table);
                cmd.Parameters.AddWithValue("@column", column);
                return Convert.ToString(await cmd.ExecuteScalarAsync(token).ConfigureAwait(false)) ?? String.Empty;
            }
        }

        private static async Task ExecuteAsync(NpgsqlConnection conn, string sql, CancellationToken token)
        {
            using (NpgsqlCommand cmd = new NpgsqlCommand(sql, conn))
                await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
    }
}
