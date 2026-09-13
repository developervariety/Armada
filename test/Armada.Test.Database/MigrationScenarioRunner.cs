namespace Armada.Test.Database
{
    using System;
    using System.Collections.Generic;
    using System.Data.Common;
    using System.Globalization;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Settings;
    using Microsoft.Data.Sqlite;
    using Microsoft.Data.SqlClient;
    using MySqlConnector;
    using Npgsql;
    using SyslogLogging;

    /// <summary>
    /// Fault and upgrade fixtures. Each scenario requires an empty, dedicated test database.
    /// Fixtures run the preserved migrations; they never fabricate applied version records.
    /// </summary>
    internal sealed class MigrationScenarioRunner
    {
        private readonly DatabaseSettings _Settings;

        internal MigrationScenarioRunner(DatabaseSettings settings)
        {
            _Settings = settings;
        }

        internal async Task RunAsync(string scenario, CancellationToken token)
        {
            using (DbConnection connection = CreateConnection(_Settings))
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                string census = _Settings.Type switch
                {
                    DatabaseTypeEnum.Sqlite => "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%';",
                    DatabaseTypeEnum.Postgresql => "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema=current_schema();",
                    DatabaseTypeEnum.Mysql => "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema=DATABASE();",
                    DatabaseTypeEnum.SqlServer => "SELECT COUNT(*) FROM sys.tables;",
                    _ => throw new NotSupportedException()
                };
                DatabaseAssert.Equal(0L, Convert.ToInt64(await ScalarAsync(connection, census, token).ConfigureAwait(false)), "Scenario requires an empty database");
            }

            if (scenario == "mysql-compat")
                await new MysqlLegacyCompatibilityTests(_Settings).VerifyAsync(token).ConfigureAwait(false);

            if (scenario == "sqlserver-corrections")
                await new SqlServerCorrectionTests(_Settings).VerifyAsync(token).ConfigureAwait(false);

            Dictionary<int, string> before = new Dictionary<int, string>();
            if (scenario == "concurrent-fresh")
            {
                using (Barrier race = new Barrier(2))
                using (DatabaseDriver first = CreateDriver())
                using (DatabaseDriver second = CreateDriver())
                {
                    if (_Settings.Type == DatabaseTypeEnum.Sqlite)
                    {
                        Action<int, int> checkpoint = (version, ordinal) =>
                        {
                            if (version == 1 && ordinal == -1 && !race.SignalAndWait(TimeSpan.FromSeconds(10)))
                                throw new Exception("Both SQLite initializers must read the initial version before proceeding");
                        };
                        first.MigrationCheckpoint = checkpoint;
                        second.MigrationCheckpoint = checkpoint;
                    }
                    TaskCompletionSource<bool> start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    Task firstStart = Task.Run(async () => { await start.Task.ConfigureAwait(false); await first.InitializeAsync(token).ConfigureAwait(false); }, token);
                    Task secondStart = Task.Run(async () => { await start.Task.ConfigureAwait(false); await second.InitializeAsync(token).ConfigureAwait(false); }, token);
                    start.SetResult(true);
                    await Task.WhenAll(firstStart, secondStart).ConfigureAwait(false);
                }
            }
            else if (scenario != "fresh" && scenario != "catalog-guards" && scenario != "mysql-compat" && scenario != "sqlserver-corrections")
            {
                bool stopped = false;
                using (DatabaseDriver driver = CreateDriver())
                {
                    driver.MigrationCheckpoint = (version, ordinal) =>
                    {
                        if ((scenario == "upgrade-51" || scenario == "partial-52") && version == 52 && ordinal == -1
                            || scenario == "partial-first" && version == 1 && ordinal == 0
                            || scenario == "partial-identity" && version == 52 && ordinal == -4)
                            throw new FixtureStopException();
                    };
                    try { await driver.InitializeAsync(token).ConfigureAwait(false); }
                    catch (FixtureStopException) { stopped = true; }
                }
                DatabaseAssert.True(stopped, "Selected migration fault point was reached");
                before = await ReadHistoryAsync(token).ConfigureAwait(false);
                int expectedVersion = scenario == "partial-first" ? 0 : 51;
                DatabaseAssert.Equal(expectedVersion, before.Count == 0 ? 0 : System.Linq.Enumerable.Max(before.Keys), "Failure did not advance applied version");
                if (scenario == "partial-identity")
                {
                    using (DbConnection connection = CreateConnection(_Settings))
                    {
                        await connection.OpenAsync(token).ConfigureAwait(false);
                        DatabaseAssert.Equal(0L, Convert.ToInt64(await ScalarAsync(connection, "SELECT COUNT(*) FROM users WHERE id='default';", token).ConfigureAwait(false)), "Interrupted first-boot user rolls back");
                        DatabaseAssert.Equal(0L, Convert.ToInt64(await ScalarAsync(connection, "SELECT COUNT(*) FROM credentials WHERE id='default';", token).ConfigureAwait(false)), "Interrupted first-boot credential absent");
                    }
                }
                if (scenario == "partial-52")
                {
                    using (DbConnection connection = CreateConnection(_Settings))
                    {
                        await connection.OpenAsync(token).ConfigureAwait(false);
                        using (DbCommand command = connection.CreateCommand())
                        {
                            command.CommandText = "INSERT INTO objectives(id,title,status,kind,priority,backlog_state,effort,created_utc,last_update_utc) VALUES ('historical-null-objective','Historical null scope','Draft','Feature','P2','Inbox','M',@created,@created);";
                            DateTime timestamp = new DateTime(2024, 2, 3, 4, 5, 6, DateTimeKind.Utc);
                            Add(command, "@created", _Settings.Type == DatabaseTypeEnum.Sqlite || _Settings.Type == DatabaseTypeEnum.SqlServer
                                ? (object)timestamp.ToString("o", CultureInfo.InvariantCulture) : timestamp);
                            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                        }
                    }
                    stopped = false;
                    using (DatabaseDriver driver = CreateDriver())
                    {
                        driver.MigrationCheckpoint = (version, ordinal) =>
                        {
                            if (version == 52 && ordinal == 0) throw new FixtureStopException();
                        };
                        try { await driver.InitializeAsync(token).ConfigureAwait(false); }
                        catch (FixtureStopException) { stopped = true; }
                    }
                    DatabaseAssert.True(stopped, "Populated DML failure point reached");
                    AssertHistory(before, await ReadHistoryAsync(token).ConfigureAwait(false));
                    using (DbConnection connection = CreateConnection(_Settings))
                    {
                        await connection.OpenAsync(token).ConfigureAwait(false);
                        DatabaseAssert.Equal(1L, Convert.ToInt64(await ScalarAsync(connection,
                            "SELECT COUNT(*) FROM objectives WHERE id='historical-null-objective' AND tenant_id IS NULL AND user_id IS NULL;", token).ConfigureAwait(false)), "Interrupted DML rolls back data");
                    }
                }
                if (scenario == "upgrade-51")
                {
                    // This column exists at v51 on every provider. Preserve an independent Unicode
                    // data sentinel across the remaining migrations, not only their version number.
                    using (DbConnection connection = CreateConnection(_Settings))
                    {
                        await connection.OpenAsync(token).ConfigureAwait(false);
                        using (DbCommand command = connection.CreateCommand())
                        {
                            command.CommandText = "INSERT INTO tenants (id,name,active,created_utc,last_update_utc) VALUES (@id,@name,@active,@created,@created);";
                            Add(command, "@id", "historical-tenant-雪");
                            Add(command, "@name", "Historical Unicode 雪 🚢");
                            Add(command, "@active", _Settings.Type == DatabaseTypeEnum.Postgresql ? (object)true : 1);
                            DateTime timestamp = new DateTime(2024, 2, 3, 4, 5, 6, DateTimeKind.Utc).AddTicks(1234560);
                            Add(command, "@created", _Settings.Type == DatabaseTypeEnum.Sqlite || _Settings.Type == DatabaseTypeEnum.SqlServer ? (object)timestamp.ToString("o", CultureInfo.InvariantCulture) : timestamp);
                            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                        }
                    }
                }
            }

            using (DatabaseDriver driver = await DatabaseDriverFactory.CreateAndInitializeAsync(_Settings, token).ConfigureAwait(false))
            {
                Dictionary<int, string> after = await ReadHistoryAsync(token).ConfigureAwait(false);
                if (scenario == "partial-52")
                {
                    using (DbConnection connection = CreateConnection(_Settings))
                    {
                        await connection.OpenAsync(token).ConfigureAwait(false);
                        DatabaseAssert.Equal(1L, Convert.ToInt64(await ScalarAsync(connection,
                            "SELECT COUNT(*) FROM objectives WHERE id='historical-null-objective' AND tenant_id='default' AND user_id='default';", token).ConfigureAwait(false)), "Restart completes both normalization statements");
                    }
                }

                AssertHistory(before, after);
                if (scenario == "catalog-guards")
                    await new PrerequisiteGuardTests(_Settings).VerifyAsync(token).ConfigureAwait(false);
                if (scenario == "upgrade-51")
                {
                    Armada.Core.Models.TenantMetadata tenant = DatabaseAssert.NotNull(await driver.Tenants.ReadAsync("historical-tenant-雪", token).ConfigureAwait(false), "Historical tenant survives");
                    DatabaseAssert.Equal("Historical Unicode 雪 🚢", tenant.Name, "Historical Unicode content");
                    DatabaseAssert.True(tenant.Active, "Historical active state");
                }
                if (scenario == "partial-identity")
                {
                    using (DbConnection connection = CreateConnection(_Settings))
                    {
                        await connection.OpenAsync(token).ConfigureAwait(false);
                        DatabaseAssert.Equal(1L, Convert.ToInt64(await ScalarAsync(connection, "SELECT COUNT(*) FROM users WHERE id='default';", token).ConfigureAwait(false)), "Restart creates default user");
                        DatabaseAssert.Equal(1L, Convert.ToInt64(await ScalarAsync(connection, "SELECT COUNT(*) FROM credentials WHERE id='default';", token).ConfigureAwait(false)), "Restart creates default credential");
                        using (DbCommand command = connection.CreateCommand())
                        {
                            command.CommandText = "DELETE FROM credentials WHERE id='default';";
                            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                        }
                        await driver.InitializeAsync(token).ConfigureAwait(false);
                        DatabaseAssert.Equal(0L, Convert.ToInt64(await ScalarAsync(connection, "SELECT COUNT(*) FROM credentials WHERE id='default';", token).ConfigureAwait(false)), "Deliberately removed credential is not recreated");
                    }
                }
                await driver.InitializeAsync(token).ConfigureAwait(false);
                Dictionary<int, string> reopened = await ReadHistoryAsync(token).ConfigureAwait(false);
                DatabaseAssert.Equal(after.Count, reopened.Count, "Repeated startup history count");
                AssertHistory(after, reopened);
                await new SchemaVerificationTests(_Settings).VerifyAsync(token).ConfigureAwait(false);
            }
            Console.WriteLine("PASS migration scenario " + scenario + ": recovery, schema and unchanged applied history");
        }

        internal static DbConnection CreateConnection(DatabaseSettings settings)
        {
            return settings.Type switch
            {
                DatabaseTypeEnum.Sqlite => new SqliteConnection(settings.GetConnectionString()),
                DatabaseTypeEnum.Postgresql => new NpgsqlConnection(settings.GetConnectionString()),
                DatabaseTypeEnum.Mysql => new MySqlConnection(settings.GetConnectionString()),
                DatabaseTypeEnum.SqlServer => new SqlConnection(settings.GetConnectionString()),
                _ => throw new NotSupportedException()
            };
        }

        private DatabaseDriver CreateDriver()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return DatabaseDriverFactory.Create(_Settings, logging);
        }

        internal async Task<Dictionary<int, string>> ReadHistoryAsync(CancellationToken token)
        {
            Dictionary<int, string> rows = new Dictionary<int, string>();
            using (DbConnection connection = CreateConnection(_Settings))
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (DbCommand command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT version,description,applied_utc FROM schema_migrations ORDER BY version;";
                    using (DbDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                            rows.Add(Convert.ToInt32(reader[0]), Convert.ToString(reader[1], CultureInfo.InvariantCulture) + "|"
                                + (reader[2] is DateTime timestamp ? timestamp.ToString("o", CultureInfo.InvariantCulture) : Convert.ToString(reader[2], CultureInfo.InvariantCulture)));
                    }
                }
            }
            return rows;
        }

        internal static void AssertHistory(Dictionary<int, string> before, Dictionary<int, string> after)
        {
            foreach (KeyValuePair<int, string> row in before)
            {
                DatabaseAssert.True(after.ContainsKey(row.Key), "Applied history retained " + row.Key);
                DatabaseAssert.Equal(row.Value, after[row.Key], "Applied description and timestamp " + row.Key);
            }
        }

        private static async Task<object> ScalarAsync(DbConnection connection, string sql, CancellationToken token)
        {
            using (DbCommand command = connection.CreateCommand())
            {
                command.CommandText = sql;
                return await command.ExecuteScalarAsync(token).ConfigureAwait(false);
            }
        }

        private static void Add(DbCommand command, string name, object value)
        {
            DbParameter parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }

        private sealed class FixtureStopException : Exception { }
    }
}
