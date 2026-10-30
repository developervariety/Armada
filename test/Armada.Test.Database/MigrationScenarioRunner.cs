namespace Armada.Test.Database
{
    using System;
    using System.Collections.Generic;
    using System.Data.Common;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
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

            if (scenario == "generated-artifact-migration")
            {
                await new GeneratedArtifactMigrationTests(_Settings).VerifyAsync(token).ConfigureAwait(false);
                return;
            }

            if (scenario == "admission-migration")
                await new MissionAdmissionMigrationTests(_Settings).VerifyAsync(token).ConfigureAwait(false);

            if (scenario == "model-endpoint-migration")
                await new ModelEndpointMigrationTests(_Settings).VerifyAsync(token).ConfigureAwait(false);

            if (scenario == "model-endpoint-guards")
            {
                await new ModelEndpointMigrationTests(_Settings).VerifyGuardAsync(token).ConfigureAwait(false);
                return;
            }

            if (scenario == "harbor-enrollment-migration")
            {
                await new HarborRunnerEnrollmentMigrationTests(_Settings).VerifyAsync(token).ConfigureAwait(false);
                return;
            }

            if (scenario == "harbor-enrollment-combined")
            {
                await new HarborRunnerEnrollmentCombinedMigrationTests(_Settings).VerifyAsync(token).ConfigureAwait(false);
                Console.WriteLine("PASS migration scenario " + scenario + ": ordered faults, partial table rejection, restart, persistence and conditional writes");
                return;
            }

            if (scenario == "skipped-version")
            {
                await new SkippedMigrationVersionTests(_Settings).VerifyAsync(token).ConfigureAwait(false);
                return;
            }

            if (scenario == "harbor-enrollment-guards")
            {
                await new HarborRunnerEnrollmentSchemaGuardTests(_Settings).VerifyAsync(token).ConfigureAwait(false);
                return;
            }

            if (scenario == "postgres-legacy")
                await new PostgresqlLegacySchemaTests(_Settings).VerifyAsync(token).ConfigureAwait(false);

            if (scenario == "anchor-migration")
                await new DockAnchorMigrationTests(_Settings).VerifyAsync(token).ConfigureAwait(false);

            if (scenario == "memory-migration")
                await new MemoryMigrationTests(_Settings).VerifyAsync(token).ConfigureAwait(false);

            if (scenario == "catalog-column-prune")
                await new CatalogAndColumnPruneMigrationTests(_Settings).VerifyAsync(token).ConfigureAwait(false);
            if (scenario == "reviewer-persona-prune")
                await new ReviewerPersonaPruneMigrationTests(_Settings).VerifyAsync(token).ConfigureAwait(false);
            if (scenario == "mission-input-wait-cancel")
                await new MissionInputWaitCancelMigrationTests(_Settings).VerifyAsync(token).ConfigureAwait(false);
            if (scenario == "ownership-migration")
                await new ConfigurationOwnershipMigrationTests(_Settings).VerifyAsync(token).ConfigureAwait(false);

            if (scenario == "backend-migration")
                await new BackendMetadataMigrationTests(_Settings).VerifyAsync(token).ConfigureAwait(false);

            if (scenario == "preview-migration")
                await new VesselPreviewMigrationTests(_Settings).VerifyAsync(token).ConfigureAwait(false);

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
            else if (scenario != "fresh" && scenario != "catalog-guards" && scenario != "mysql-compat" && scenario != "sqlserver-corrections" && scenario != "preview-migration" && scenario != "backend-migration" && scenario != "anchor-migration" && scenario != "memory-migration" && scenario != "ownership-migration" && scenario != "catalog-column-prune" && scenario != "reviewer-persona-prune" && scenario != "mission-input-wait-cancel" && scenario != "postgres-legacy" && scenario != "admission-migration" && scenario != "model-endpoint-migration")
            {
                int anchorVersion = _Settings.Type switch
                {
                    DatabaseTypeEnum.Sqlite => 85, DatabaseTypeEnum.Postgresql => 86,
                    DatabaseTypeEnum.Mysql => 77, DatabaseTypeEnum.SqlServer => 80,
                    _ => throw new NotSupportedException()
                };
                bool stopped = false;
                using (DatabaseDriver driver = CreateDriver())
                {
                    driver.MigrationCheckpoint = (version, ordinal) =>
                    {
                        if ((scenario == "upgrade-51" || scenario == "partial-52") && version == 52 && ordinal == -1
                            || scenario == "partial-first" && version == 1 && ordinal == 0
                            || scenario == "partial-anchor" && version == anchorVersion && ordinal == 0
                            || scenario == "partial-identity" && version == 52 && ordinal == -4)
                            throw new FixtureStopException();
                    };
                    try { await driver.InitializeAsync(token).ConfigureAwait(false); }
                    catch (FixtureStopException) { stopped = true; }
                }
                DatabaseAssert.True(stopped, "Selected migration fault point was reached");
                before = await ReadHistoryAsync(token).ConfigureAwait(false);
                int appliedMaximum = before.Count == 0 ? 0 : System.Linq.Enumerable.Max(before.Keys);
                if (scenario == "partial-anchor")
                    DatabaseAssert.True(!before.ContainsKey(anchorVersion) && appliedMaximum < anchorVersion, "Failure did not record the anchor version");
                else
                    DatabaseAssert.Equal(scenario == "partial-first" ? 0 : 51, appliedMaximum, "Failure did not advance applied version");
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
                        await SeedHistoricalBackupRowsAsync(connection, token).ConfigureAwait(false);
                        await AssertHistoricalBackupRowsAsync(connection, token).ConfigureAwait(false);
                    }

                    await VerifyHistoricalBackupUpgradeAsync(before, token).ConfigureAwait(false);
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
                    using (DbConnection connection = CreateConnection(_Settings))
                    {
                        await connection.OpenAsync(token).ConfigureAwait(false);
                        await AssertHistoricalBackupRowsAsync(connection, token).ConfigureAwait(false);
                    }
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
                if (scenario == "upgrade-51")
                {
                    using (DbConnection connection = CreateConnection(_Settings))
                    {
                        await connection.OpenAsync(token).ConfigureAwait(false);
                        await AssertHistoricalBackupRowsAsync(connection, token).ConfigureAwait(false);
                    }
                }
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

        internal DatabaseDriver CreateDriver()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return DatabaseDriverFactory.Create(_Settings, logging);
        }

        private async Task VerifyHistoricalBackupUpgradeAsync(Dictionary<int, string> expectedHistory, CancellationToken token)
        {
            string root = Path.Combine(Path.GetTempPath(), "armada-historical-backup-" + Guid.NewGuid().ToString("N"));
            string nativeDirectory = Path.Combine(root, "native");
            string? sqlServerDirectory = Environment.GetEnvironmentVariable("ARMADA_SELF_DEPLOY_SQLSERVER_BACKUP_DIRECTORY");
            if (_Settings.Type == DatabaseTypeEnum.SqlServer && String.IsNullOrWhiteSpace(sqlServerDirectory))
                throw new InvalidOperationException("ARMADA_SELF_DEPLOY_SQLSERVER_BACKUP_DIRECTORY is required for the historical SQL Server backup proof.");

            SelfDeployDatabaseBackupProvider provider = new SelfDeployDatabaseBackupProvider(
                _Settings, nativeDirectory, sqlServerBackupDirectory: sqlServerDirectory);
            SelfDeployDatabaseBackupResult? restored = null;
            DatabaseSettings? restoredSettings = null;
            int restoredVersion = 0;
            int gateVersion = 0;
            int upgradedVersion = 0;
            int reopenedVersion = 0;
            try
            {
                restored = await provider.CreateAndVerifyAsync(token).ConfigureAwait(false);
                DatabaseAssert.True(restored.BackupValidated, "Historical native backup validated: " + restored.FailureReason);
                DatabaseAssert.True(restored.RestoreVerified, "Historical isolated restore verified: " + restored.FailureReason);
                DatabaseAssert.True(provider.OwnsTarget(restored), "Historical restore target has provider ownership");

                restoredSettings = CloneSettingsForTarget(_Settings, restored.IsolatedTarget);
                MigrationScenarioRunner restoredRunner = new MigrationScenarioRunner(restoredSettings);
                Dictionary<int, string> restoredHistory = await restoredRunner.ReadHistoryAsync(token).ConfigureAwait(false);
                restoredVersion = restoredHistory.Count == 0 ? 0 : System.Linq.Enumerable.Max(restoredHistory.Keys);
                DatabaseAssert.Equal(51, restoredVersion, "Restored target is still at v51 before startup");
                DatabaseAssert.Equal(expectedHistory.Count, restoredHistory.Count, "Restored v51 history row count");
                AssertHistory(expectedHistory, restoredHistory);
                using (DbConnection connection = CreateConnection(restoredSettings))
                {
                    await connection.OpenAsync(token).ConfigureAwait(false);
                    await AssertHistoricalBackupRowsAsync(connection, token).ConfigureAwait(false);
                }

                ArmadaSettings migrationSettings = new ArmadaSettings
                {
                    DataDirectory = Path.Combine(root, "migration-data"),
                    Database = restoredSettings
                };
                migrationSettings.SelfDeploy.SqlServerBackupDirectory = sqlServerDirectory;
                LoggingModule migrationLogging = new LoggingModule();
                migrationLogging.Settings.EnableConsole = false;
                using (DatabaseDriver pendingDriver = restoredRunner.CreateDriver())
                {
                    DatabaseBackupResult? pendingBackup = await new MigrationBackupService(migrationSettings, migrationLogging)
                        .PrepareAsync(pendingDriver, token).ConfigureAwait(false);
                    DatabaseAssert.NotNull(pendingBackup, "Pending historical upgrade requires a native backup");
                    DatabaseAssert.Equal(51, pendingBackup!.SchemaVersion, "Pre-migration backup reports historical v51");
                    gateVersion = pendingBackup.SchemaVersion;
                    DatabaseAssert.True(File.Exists(pendingBackup.Path), "Pre-migration archive retained");
                    DatabaseAssert.Equal(51, await pendingDriver.GetSchemaVersionAsync(token).ConfigureAwait(false),
                        "The backup gate leaves the historical ledger unchanged");
                }

                Dictionary<int, string> upgradedHistory;
                using (DatabaseDriver upgradeDriver = restoredRunner.CreateDriver())
                {
                    await upgradeDriver.InitializeAsync(token).ConfigureAwait(false);
                    upgradedHistory = await restoredRunner.ReadHistoryAsync(token).ConfigureAwait(false);
                    upgradedVersion = System.Linq.Enumerable.Max(upgradedHistory.Keys);
                    DatabaseAssert.Equal(LatestMigrationVersion(), upgradedVersion, "First startup applies every defined migration");
                    AssertHistory(restoredHistory, upgradedHistory);
                    using (DbConnection connection = CreateConnection(restoredSettings))
                    {
                        await connection.OpenAsync(token).ConfigureAwait(false);
                        await AssertHistoricalBackupRowsAsync(connection, token).ConfigureAwait(false);
                    }
                }

                using (DatabaseDriver reopenedDriver = restoredRunner.CreateDriver())
                {
                    await reopenedDriver.InitializeAsync(token).ConfigureAwait(false);
                    Dictionary<int, string> reopenedHistory = await restoredRunner.ReadHistoryAsync(token).ConfigureAwait(false);
                    reopenedVersion = reopenedHistory.Count == 0 ? 0 : System.Linq.Enumerable.Max(reopenedHistory.Keys);
                    DatabaseAssert.Equal(upgradedHistory.Count, reopenedHistory.Count, "Second startup adds no migration history");
                    AssertHistory(upgradedHistory, reopenedHistory);
                    using (DbConnection connection = CreateConnection(restoredSettings))
                    {
                        await connection.OpenAsync(token).ConfigureAwait(false);
                        await AssertHistoricalBackupRowsAsync(connection, token).ConfigureAwait(false);
                    }
                    await new SchemaVerificationTests(restoredSettings).VerifyAsync(token).ConfigureAwait(false);
                }
                Console.WriteLine("Historical backup proof: provider=" + _Settings.Type + " source=v51 restored=v" + restoredVersion
                    + " gate=v" + gateVersion + " upgraded=v" + upgradedVersion + " reopened=v" + reopenedVersion);
            }
            finally
            {
                if (restoredSettings != null) ClearTargetPool(restoredSettings);
                if (restored != null && provider.OwnsTarget(restored))
                    DatabaseAssert.True(await provider.CleanupAsync(restored, CancellationToken.None).ConfigureAwait(false), "Owned historical restore target cleaned");
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }

        private int LatestMigrationVersion()
        {
            List<SchemaMigration> migrations = _Settings.Type switch
            {
                DatabaseTypeEnum.Sqlite => Armada.Core.Database.Sqlite.Queries.TableQueries.GetMigrations(),
                DatabaseTypeEnum.Postgresql => Armada.Core.Database.Postgresql.Queries.TableQueries.GetMigrations(),
                DatabaseTypeEnum.Mysql => Armada.Core.Database.Mysql.MysqlDatabaseDriver.GetMigrations(),
                DatabaseTypeEnum.SqlServer => Armada.Core.Database.SqlServer.Queries.TableQueries.GetMigrations(),
                _ => throw new NotSupportedException()
            };
            return migrations.Max(migration => migration.Version);
        }

        private static void ClearTargetPool(DatabaseSettings settings)
        {
            switch (settings.Type)
            {
                case DatabaseTypeEnum.Sqlite:
                    using (SqliteConnection connection = new SqliteConnection(settings.GetConnectionString())) SqliteConnection.ClearPool(connection);
                    break;
                case DatabaseTypeEnum.Postgresql:
                    using (NpgsqlConnection connection = new NpgsqlConnection(settings.GetConnectionString())) NpgsqlConnection.ClearPool(connection);
                    break;
                case DatabaseTypeEnum.Mysql:
                    using (MySqlConnection connection = new MySqlConnection(settings.GetConnectionString())) MySqlConnection.ClearPool(connection);
                    break;
                case DatabaseTypeEnum.SqlServer:
                    using (SqlConnection connection = new SqlConnection(settings.GetConnectionString())) SqlConnection.ClearPool(connection);
                    break;
            }
        }

        private static DatabaseSettings CloneSettingsForTarget(DatabaseSettings source, string target)
        {
            DatabaseSettings clone = new DatabaseSettings
            {
                Type = source.Type,
                Hostname = source.Hostname,
                Port = source.Port,
                Username = source.Username,
                Password = source.Password,
                DatabaseName = source.DatabaseName,
                Schema = source.Schema,
                RequireEncryption = source.RequireEncryption,
                LogQueries = source.LogQueries,
                MinPoolSize = source.MinPoolSize,
                MaxPoolSize = source.MaxPoolSize,
                ConnectionLifetimeSeconds = source.ConnectionLifetimeSeconds,
                ConnectionIdleTimeoutSeconds = source.ConnectionIdleTimeoutSeconds
            };
            if (source.Type == DatabaseTypeEnum.Sqlite) clone.Filename = target;
            else clone.DatabaseName = target;
            return clone;
        }

        private async Task SeedHistoricalBackupRowsAsync(DbConnection connection, CancellationToken token)
        {
            DateTime created = HistoricalBackupTimestamp();
            DateTime updated = created.AddMinutes(7);
            object createdValue = StoredTimestamp(created);
            object updatedValue = StoredTimestamp(updated);
            object active = _Settings.Type == DatabaseTypeEnum.Postgresql ? (object)true : 1;
            using (DbCommand command = connection.CreateCommand())
            {
                command.CommandText = "INSERT INTO tenants (id,name,active,created_utc,last_update_utc) VALUES (@tenant_id,@tenant_name,@active,@created,@updated);";
                Add(command, "@tenant_id", "backup-tenant");
                Add(command, "@tenant_name", "Backup tenant 雪 🚢");
                Add(command, "@active", active);
                Add(command, "@created", createdValue);
                Add(command, "@updated", updatedValue);
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
            using (DbCommand command = connection.CreateCommand())
            {
                command.CommandText = "INSERT INTO users (id,tenant_id,email,password_sha256,first_name,last_name,is_admin,is_tenant_admin,active,created_utc,last_update_utc) VALUES (@user_id,@tenant_id,@email,@password,@first_name,NULL,@not_admin,@not_admin,@active,@created,@updated);";
                Add(command, "@tenant_id", "backup-tenant");
                Add(command, "@user_id", "backup-user");
                Add(command, "@email", "backup-restore-雪@example.test");
                Add(command, "@password", new string('a', 64));
                Add(command, "@first_name", "Zoë 雪");
                Add(command, "@not_admin", _Settings.Type == DatabaseTypeEnum.Postgresql ? (object)false : 0);
                Add(command, "@active", active);
                Add(command, "@created", createdValue);
                Add(command, "@updated", updatedValue);
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
            using (DbCommand command = connection.CreateCommand())
            {
                command.CommandText = "INSERT INTO credentials (id,tenant_id,user_id,name,bearer_token,active,created_utc,last_update_utc) VALUES (@credential_id,@tenant_id,@user_id,NULL,@token,@active,@created,@updated);";
                Add(command, "@tenant_id", "backup-tenant");
                Add(command, "@user_id", "backup-user");
                Add(command, "@credential_id", "backup-credential");
                Add(command, "@token", "historical-backup-token-test-only");
                Add(command, "@active", active);
                Add(command, "@created", createdValue);
                Add(command, "@updated", updatedValue);
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
            using (DbCommand command = connection.CreateCommand())
            {
                command.CommandText = "INSERT INTO fleets (id,tenant_id,user_id,name,description,active,created_utc,last_update_utc) VALUES (@fleet_id,@tenant_id,@user_id,@fleet_name,NULL,@active,@created,@updated);";
                Add(command, "@tenant_id", "backup-tenant");
                Add(command, "@user_id", "backup-user");
                Add(command, "@fleet_id", "backup-fleet");
                Add(command, "@fleet_name", "Backup fleet 雪");
                Add(command, "@active", active);
                Add(command, "@created", createdValue);
                Add(command, "@updated", updatedValue);
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
        }

        private async Task AssertHistoricalBackupRowsAsync(DbConnection connection, CancellationToken token)
        {
            using (DbCommand command = connection.CreateCommand())
            {
                command.CommandText = @"SELECT t.name,t.active,t.created_utc,t.last_update_utc,
u.email,u.first_name,u.last_name,u.created_utc,u.last_update_utc,
c.name,c.bearer_token,c.active,c.created_utc,c.last_update_utc,
f.name,f.description,f.active,f.created_utc,f.last_update_utc,f.tenant_id,f.user_id,
u.password_sha256,u.is_admin,u.is_tenant_admin,u.active
FROM tenants t JOIN users u ON u.tenant_id=t.id
JOIN credentials c ON c.tenant_id=t.id AND c.user_id=u.id
JOIN fleets f ON f.tenant_id=t.id AND f.user_id=u.id
WHERE t.id='backup-tenant' AND u.id='backup-user' AND c.id='backup-credential' AND f.id='backup-fleet';";
                using (DbDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
                {
                    DatabaseAssert.True(await reader.ReadAsync(token).ConfigureAwait(false), "Linked historical tenant, user, credential and fleet rows exist");
                    DatabaseAssert.Equal("Backup tenant 雪 🚢", Convert.ToString(reader[0], CultureInfo.InvariantCulture), "Historical tenant Unicode name");
                    DatabaseAssert.True(Convert.ToBoolean(reader[1], CultureInfo.InvariantCulture), "Historical tenant active state");
                    AssertHistoricalTimestamp(reader[2], HistoricalBackupTimestamp(), "Tenant created timestamp");
                    AssertHistoricalTimestamp(reader[3], HistoricalBackupTimestamp().AddMinutes(7), "Tenant update timestamp");
                    DatabaseAssert.Equal("backup-restore-雪@example.test", Convert.ToString(reader[4], CultureInfo.InvariantCulture), "Historical user email");
                    DatabaseAssert.Equal("Zoë 雪", Convert.ToString(reader[5], CultureInfo.InvariantCulture), "Historical user Unicode first name");
                    DatabaseAssert.True(reader.IsDBNull(6), "Historical user nullable last name");
                    AssertHistoricalTimestamp(reader[7], HistoricalBackupTimestamp(), "User created timestamp");
                    AssertHistoricalTimestamp(reader[8], HistoricalBackupTimestamp().AddMinutes(7), "User update timestamp");
                    DatabaseAssert.True(reader.IsDBNull(9), "Historical credential nullable name");
                    DatabaseAssert.Equal("historical-backup-token-test-only", Convert.ToString(reader[10], CultureInfo.InvariantCulture), "Historical credential token");
                    DatabaseAssert.True(Convert.ToBoolean(reader[11], CultureInfo.InvariantCulture), "Historical credential active state");
                    AssertHistoricalTimestamp(reader[12], HistoricalBackupTimestamp(), "Credential created timestamp");
                    AssertHistoricalTimestamp(reader[13], HistoricalBackupTimestamp().AddMinutes(7), "Credential update timestamp");
                    DatabaseAssert.Equal("Backup fleet 雪", Convert.ToString(reader[14], CultureInfo.InvariantCulture), "Historical fleet name");
                    DatabaseAssert.True(reader.IsDBNull(15), "Historical fleet nullable description");
                    DatabaseAssert.True(Convert.ToBoolean(reader[16], CultureInfo.InvariantCulture), "Historical fleet active state");
                    AssertHistoricalTimestamp(reader[17], HistoricalBackupTimestamp(), "Fleet created timestamp");
                    AssertHistoricalTimestamp(reader[18], HistoricalBackupTimestamp().AddMinutes(7), "Fleet update timestamp");
                    DatabaseAssert.Equal("backup-tenant", Convert.ToString(reader[19], CultureInfo.InvariantCulture), "Fleet tenant foreign key");
                    DatabaseAssert.Equal("backup-user", Convert.ToString(reader[20], CultureInfo.InvariantCulture), "Fleet user foreign key");
                    DatabaseAssert.Equal(new String('a', 64), Convert.ToString(reader[21], CultureInfo.InvariantCulture), "Historical password storage bytes");
                    DatabaseAssert.True(!Convert.ToBoolean(reader[22], CultureInfo.InvariantCulture), "Historical user is not a global administrator");
                    DatabaseAssert.True(!Convert.ToBoolean(reader[23], CultureInfo.InvariantCulture), "Historical user is not a tenant administrator");
                    DatabaseAssert.True(Convert.ToBoolean(reader[24], CultureInfo.InvariantCulture), "Historical user active state");
                    DatabaseAssert.True(!await reader.ReadAsync(token).ConfigureAwait(false), "Historical sentinel join has no duplicate rows");
                }
            }
        }

        private object StoredTimestamp(DateTime value)
        {
            return _Settings.Type == DatabaseTypeEnum.Sqlite || _Settings.Type == DatabaseTypeEnum.SqlServer
                ? value.ToString("o", CultureInfo.InvariantCulture)
                : value;
        }

        private static DateTime HistoricalBackupTimestamp()
        {
            return new DateTime(2024, 2, 3, 4, 5, 6, DateTimeKind.Utc).AddTicks(1234560);
        }

        private static void AssertHistoricalTimestamp(object value, DateTime expected, string label)
        {
            DateTime actual = value is DateTime timestamp
                ? DateTime.SpecifyKind(timestamp, DateTimeKind.Utc)
                : DateTime.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal);
            DatabaseAssert.Equal(expected, actual, label);
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
