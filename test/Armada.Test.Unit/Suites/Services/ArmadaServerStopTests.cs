namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.IO.Compression;
    using System.Net;
    using System.Net.Sockets;
    using System.Reflection;
    using System.Runtime.CompilerServices;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Enums;
    using Armada.Core.Settings;
    using Armada.Server;
    using Armada.Test.Common;
    using Microsoft.Data.Sqlite;
    using SyslogLogging;

    /// <summary>
    /// Admiral shutdown: stop runs once, waits for every background loop that uses the database before
    /// disposing it, and still disposes the database when an earlier shutdown step throws.
    /// </summary>
    public class ArmadaServerStopTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Admiral Server Stop";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("Startup backs up the old SQLite schema before applying its last migration", async () =>
            {
                string tempDir = NewTempDirectory();
                string databasePath = Path.Combine(tempDir, "armada.db");
                string fleetName = "pre-migration-fleet-" + Guid.NewGuid().ToString("N");
                LoggingModule logging = CreateQuietLogging();
                ArmadaServer server = NewServer(tempDir);
                string extractedPath = Path.Combine(tempDir, "pre-migration-archive.db");
                try
                {
                    int priorVersion = await CreatePendingMigrationFixtureAsync(databasePath, fleetName, logging).ConfigureAwait(false);
                    int latestVersion = LatestMigrationVersion();

                    await server.StartAsync().ConfigureAwait(false);
                    server.Stop();

                    string backupDirectory = Path.Combine(tempDir, "backups");
                    AssertTrue(Directory.Exists(backupDirectory), "startup creates the backup directory before migration");
                    string[] archives = Directory.GetFiles(backupDirectory, "pre-migration-*.zip");
                    AssertEqual(1, archives.Length, "startup leaves one verified pre-migration archive");
                    DatabaseBackupManifest manifest = ReadBackupManifest(archives[0]);
                    AssertEqual(DatabaseTypeEnum.Sqlite, manifest.DatabaseType, "archive provider");
                    AssertEqual(priorVersion, manifest.SchemaVersion, "archive records the schema version before migration");
                    AssertTrue(manifest.BackupValidated, "archive records a validated native backup");
                    AssertTrue(manifest.RestoreVerified, "archive records an isolated restore proof");
                    AssertEqual(DatabaseBackupService.SqliteEntry, manifest.ArtifactEntry, "SQLite artifact entry");
                    File.WriteAllBytes(extractedPath, ReadZipEntry(archives[0], manifest.ArtifactEntry));
                    AssertEqual(priorVersion, await ReadSqliteSchemaVersionAsync(extractedPath).ConfigureAwait(false), "restored archive schema version");
                    AssertTrue(await SqliteHasFleetAsync(extractedPath, fleetName).ConfigureAwait(false), "restored archive keeps the representative fleet row");

                    AssertEqual(latestVersion, await ReadSqliteSchemaVersionAsync(databasePath).ConfigureAwait(false), "live database reaches the latest schema");
                    AssertTrue(await SqliteHasFleetAsync(databasePath, fleetName).ConfigureAwait(false), "live database keeps the representative fleet row");

                    ArmadaServer secondServer = NewServer(tempDir);
                    try
                    {
                        await secondServer.StartAsync().ConfigureAwait(false);
                    }
                    finally
                    {
                        secondServer.Stop();
                    }
                    AssertEqual(1, Directory.GetFiles(backupDirectory, "pre-migration-*.zip").Length,
                        "a start with no pending migrations creates no extra backup");
                }
                finally
                {
                    server.Stop();
                    if (File.Exists(extractedPath)) File.Delete(extractedPath);
                    DeleteDirectory(tempDir);
                }
            });

            await RunTest("Startup refuses a pending migration when native backup storage fails", async () =>
            {
                string tempDir = NewTempDirectory();
                string databasePath = Path.Combine(tempDir, "armada.db");
                string fleetName = "backup-failure-fleet-" + Guid.NewGuid().ToString("N");
                LoggingModule logging = CreateQuietLogging();
                ArmadaServer server = NewServer(tempDir);
                try
                {
                    int priorVersion = await CreatePendingMigrationFixtureAsync(databasePath, fleetName, logging).ConfigureAwait(false);

                    string backupDirectory = Path.Combine(tempDir, "backups");
                    File.WriteAllText(backupDirectory, "prevent native backup directory creation");

                    Exception? failure = null;
                    try
                    {
                        await server.StartAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        failure = ex;
                    }
                    finally
                    {
                        server.Stop();
                    }

                    AssertNotNull(failure, "startup must stop when its required native backup cannot be created");
                    AssertTrue(failure is DatabaseBackupException,
                        "startup reports the native backup failure, not a migration failure: " + failure!.GetType().Name + " " + failure.Message);
                    AssertEqual(priorVersion, await ReadSqliteSchemaVersionAsync(databasePath).ConfigureAwait(false),
                        "failed backup leaves the migration ledger unchanged");
                    AssertTrue(await SqliteHasFleetAsync(databasePath, fleetName).ConfigureAwait(false),
                        "failed backup preserves the representative fleet row");
                }
                finally
                {
                    server.Stop();
                    DeleteDirectory(tempDir);
                }
            });

            await RunTest("Validate-database refuses a pending migration when native backup storage fails", async () =>
            {
                string tempDir = NewTempDirectory();
                string databasePath = Path.Combine(tempDir, "armada.db");
                string fleetName = "validate-backup-failure-fleet-" + Guid.NewGuid().ToString("N");
                LoggingModule logging = CreateQuietLogging();
                try
                {
                    int priorVersion = await CreatePendingMigrationFixtureAsync(databasePath, fleetName, logging).ConfigureAwait(false);

                    string backupDirectory = Path.Combine(tempDir, "backups");
                    File.WriteAllText(backupDirectory, "prevent native backup directory creation");

                    ArmadaSettings settings = new ArmadaSettings
                    {
                        DataDirectory = tempDir,
                        DatabasePath = databasePath,
                        Database = new DatabaseSettings { Type = DatabaseTypeEnum.Sqlite, Filename = databasePath },
                        LogDirectory = Path.Combine(tempDir, "logs"),
                        DocksDirectory = Path.Combine(tempDir, "docks"),
                        ReposDirectory = Path.Combine(tempDir, "repos"),
                        SettingsFilePath = Path.Combine(tempDir, "settings.json")
                    };
                    settings.InitializeDirectories();
                    JsonSerializerOptions settingsOptions = new JsonSerializerOptions();
                    settingsOptions.Converters.Add(new JsonStringEnumConverter());
                    await File.WriteAllTextAsync(Path.Combine(tempDir, "settings.json"), JsonSerializer.Serialize(settings, settingsOptions)).ConfigureAwait(false);

                    string serverPath = FindServerAssemblyPath(AppContext.BaseDirectory);
                    ProcessStartInfo start = new ProcessStartInfo("dotnet")
                    {
                        WorkingDirectory = tempDir,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false
                    };
                    start.ArgumentList.Add(serverPath);
                    start.ArgumentList.Add("--validate-database");
                    start.Environment[Armada.Core.Constants.DataDirectoryOverrideVariable] = tempDir;

                    using (Process process = Process.Start(start)
                        ?? throw new InvalidOperationException("cannot start Armada.Server --validate-database"))
                    {
                        Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
                        Task<string> errorTask = process.StandardError.ReadToEndAsync();
                        try
                        {
                            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                        }
                        catch (TimeoutException)
                        {
                            try { process.Kill(entireProcessTree: true); }
                            catch (InvalidOperationException ex)
                            {
                                if (!process.HasExited) throw new InvalidOperationException("cannot stop Armada.Server after its validation timeout", ex);
                            }
                            await process.WaitForExitAsync().ConfigureAwait(false);
                            await Task.WhenAll(outputTask, errorTask).ConfigureAwait(false);
                            throw new TimeoutException("Armada.Server --validate-database did not exit within 30 seconds. Output: "
                                + outputTask.Result + Environment.NewLine + errorTask.Result);
                        }

                        await Task.WhenAll(outputTask, errorTask).ConfigureAwait(false);
                        string combined = outputTask.Result + Environment.NewLine + errorTask.Result;
                        AssertEqual(3, process.ExitCode, "validation exits with the backup refusal code");
                        AssertContains("DATABASE STARTUP REFUSED", combined, "validation reports the backup refusal");
                        AssertFalse(combined.Contains("DATABASE VALIDATION PASSED", StringComparison.Ordinal),
                            "validation does not report success after backup refusal");
                        AssertEqual(priorVersion, await ReadSqliteSchemaVersionAsync(databasePath).ConfigureAwait(false),
                            "validation failure leaves the migration ledger unchanged");
                        AssertTrue(await SqliteHasFleetAsync(databasePath, fleetName).ConfigureAwait(false),
                            "validation failure preserves the representative fleet row");
                    }
                }
                finally
                {
                    DeleteDirectory(tempDir);
                }
            });

            await RunTest("A repeated stop does not run the shutdown again", async () =>
            {
                string tempDir = NewTempDirectory();
                ArmadaServer server = NewServer(tempDir);
                int notifications = 0;
                server.OnStopping = () => Interlocked.Increment(ref notifications);
                try
                {
                    await server.StartAsync().ConfigureAwait(false);

                    server.Stop();
                    server.Stop();

                    AssertEqual(1, notifications, "the stop notification must fire once for two stop calls");
                    AssertTrue(IsDatabaseDisposed(server), "the first stop disposes the database");
                }
                finally
                {
                    server.Stop();
                    DeleteDirectory(tempDir);
                }
            });

            await RunTest("Stop waits for the health check loop before disposing the database", async () =>
            {
                string tempDir = NewTempDirectory();
                ArmadaServer server = NewServer(tempDir);
                try
                {
                    await server.StartAsync().ConfigureAwait(false);

                    CancellationToken serverToken = GetField<CancellationTokenSource>(server, "_TokenSource").Token;
                    Task realLoop = GetField<Task>(server, "_HealthCheckTask");
                    DatabaseDriver database = GetField<DatabaseDriver>(server, "_Database");
                    bool databaseReadAfterCancel = false;

                    // A health loop that still reads the database for a while after shutdown cancels it.
                    Task slowLoop = Task.Run(async () =>
                    {
                        try
                        {
                            await realLoop.ConfigureAwait(false);
                        }
                        catch (Exception)
                        {
                            // The real loop's own outcome is not under test.
                        }
                        try
                        {
                            await Task.Delay(Timeout.InfiniteTimeSpan, serverToken).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                        }
                        await Task.Delay(TimeSpan.FromMilliseconds(1500)).ConfigureAwait(false);
                        await database.Captains.EnumerateAsync().ConfigureAwait(false);
                        databaseReadAfterCancel = true;
                    });
                    SetField(server, "_HealthCheckTask", slowLoop);

                    server.Stop();

                    AssertTrue(slowLoop.IsCompleted, "Stop must not return before the health check loop finishes");
                    AssertFalse(slowLoop.IsFaulted, "the loop must not see a disposed database: " + slowLoop.Exception?.GetBaseException().Message);
                    AssertTrue(databaseReadAfterCancel, "the loop's last database read must succeed");
                    AssertTrue(IsDatabaseDisposed(server), "the database is disposed after the loop finishes");
                }
                finally
                {
                    server.Stop();
                    DeleteDirectory(tempDir);
                }
            });

            await RunTest("Stop waits for the Harbor job expiry loop before disposing the database", async () =>
            {
                string tempDir = NewTempDirectory();
                ArmadaServer server = NewServer(tempDir, harborEnabled: true);
                try
                {
                    await server.StartAsync().ConfigureAwait(false);

                    CancellationToken serverToken = GetField<CancellationTokenSource>(server, "_TokenSource").Token;
                    Task? realLoop = GetField<Task?>(server, "_HarborJobExpiryTask");
                    AssertNotNull(realLoop, "an enabled Harbor starts the job expiry loop");
                    DatabaseDriver database = GetField<DatabaseDriver>(server, "_Database");
                    bool databaseReadAfterCancel = false;

                    // An expiry pass that is still reading the database when shutdown cancels the loop.
                    Task slowLoop = Task.Run(async () =>
                    {
                        try
                        {
                            await realLoop!.ConfigureAwait(false);
                        }
                        catch (Exception)
                        {
                            // The real loop's own outcome is not under test.
                        }
                        await Task.Delay(TimeSpan.FromMilliseconds(1500)).ConfigureAwait(false);
                        await database.Captains.EnumerateAsync().ConfigureAwait(false);
                        databaseReadAfterCancel = true;
                    });
                    SetField(server, "_HarborJobExpiryTask", slowLoop);

                    server.Stop();

                    AssertTrue(serverToken.IsCancellationRequested, "shutdown cancels the loop's token");
                    AssertTrue(slowLoop.IsCompleted, "Stop must not return before the Harbor job expiry loop finishes");
                    AssertFalse(slowLoop.IsFaulted, "the loop must not see a disposed database: " + slowLoop.Exception?.GetBaseException().Message);
                    AssertTrue(databaseReadAfterCancel, "the loop's last database read must succeed");
                    AssertTrue(IsDatabaseDisposed(server), "the database is disposed after the loop finishes");
                }
                finally
                {
                    server.Stop();
                    DeleteDirectory(tempDir);
                }
            });

            await RunTest("Stop disposes the database and notifies even when an earlier step throws", async () =>
            {
                string tempDir = NewTempDirectory();
                ArmadaServer server = NewServer(tempDir);
                int notifications = 0;
                server.OnStopping = () => Interlocked.Increment(ref notifications);
                try
                {
                    await server.StartAsync().ConfigureAwait(false);

                    // A remote tunnel whose stop throws: its internal state was never initialized.
                    SetField(server, "_RemoteTunnel", RuntimeHelpers.GetUninitializedObject(typeof(RemoteTunnelManager)));

                    Exception? thrown = null;
                    try
                    {
                        server.Stop();
                    }
                    catch (Exception ex)
                    {
                        thrown = ex;
                    }

                    AssertNull(thrown, "a failing shutdown step is logged, not thrown: " + thrown?.Message);
                    AssertTrue(IsDatabaseDisposed(server), "the database must be disposed after a failing step");
                    AssertEqual(1, notifications, "the stop notification must still fire");
                }
                finally
                {
                    server.Stop();
                    DeleteDirectory(tempDir);
                }
            });
        }

        #region Private-Methods

        private sealed class ExpectedMigrationCheckpointException : Exception
        {
        }

        private static LoggingModule CreateQuietLogging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }

        private static async Task<int> CreatePendingMigrationFixtureAsync(
            string databasePath,
            string fleetName,
            LoggingModule logging)
        {
            List<SchemaMigration> migrations = Armada.Core.Database.Sqlite.Queries.TableQueries.GetMigrations();
            migrations.Sort((left, right) => left.Version.CompareTo(right.Version));
            SchemaMigration lastMigration = migrations[migrations.Count - 1];
            int priorVersion = migrations[migrations.Count - 2].Version;
            using (DatabaseDriver driver = DatabaseDriverFactory.Create(new DatabaseSettings
            {
                Type = DatabaseTypeEnum.Sqlite,
                Filename = databasePath
            }, logging))
            {
                bool stoppedBeforeLastMigration = false;
                driver.MigrationCheckpoint = (version, ordinal) =>
                {
                    if (version == lastMigration.Version && ordinal == -1)
                        throw new ExpectedMigrationCheckpointException();
                };
                try
                {
                    await driver.InitializeAsync().ConfigureAwait(false);
                }
                catch (ExpectedMigrationCheckpointException)
                {
                    stoppedBeforeLastMigration = true;
                }
                if (!stoppedBeforeLastMigration)
                    throw new InvalidOperationException("the fixture did not stop before the real final migration");
                if (await driver.GetSchemaVersionAsync().ConfigureAwait(false) != priorVersion)
                    throw new InvalidOperationException("the fixture did not retain the prior migration version");
                using (SqliteConnection connection = new SqliteConnection("Data Source=" + databasePath + ";Pooling=False"))
                {
                    await connection.OpenAsync().ConfigureAwait(false);
                    using (SqliteCommand command = connection.CreateCommand())
                    {
                        command.CommandText = "INSERT INTO fleets (id, name, active, created_utc, last_update_utc) VALUES (@id, @name, 1, @utc, @utc);";
                        command.Parameters.AddWithValue("@id", "flt_" + Guid.NewGuid().ToString("N"));
                        command.Parameters.AddWithValue("@name", fleetName);
                        command.Parameters.AddWithValue("@utc", DateTime.UtcNow.ToString("o"));
                        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
                    }
                }
            }
            return priorVersion;
        }

        private static int LatestMigrationVersion()
        {
            List<SchemaMigration> migrations = Armada.Core.Database.Sqlite.Queries.TableQueries.GetMigrations();
            migrations.Sort((left, right) => left.Version.CompareTo(right.Version));
            return migrations[migrations.Count - 1].Version;
        }

        private static string FindRepositoryRoot(string baseDirectory)
        {
            DirectoryInfo? directory = new DirectoryInfo(baseDirectory);
            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "src", "Armada.sln"))) return directory.FullName;
                directory = directory.Parent;
            }
            throw new DirectoryNotFoundException("Cannot locate the repository from " + baseDirectory);
        }

        private static string FindServerAssemblyPath(string baseDirectory)
        {
            DirectoryInfo output = new DirectoryInfo(baseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            string framework = output.Name;
            string configuration = output.Parent?.Name ?? throw new DirectoryNotFoundException("Cannot locate build configuration from " + baseDirectory);
            string path = Path.Combine(FindRepositoryRoot(baseDirectory), "src", "Armada.Server", "bin", configuration, framework, "Armada.Server.dll");
            if (!File.Exists(path)) throw new FileNotFoundException("Armada.Server was not built", path);
            return path;
        }

        private static DatabaseBackupManifest ReadBackupManifest(string archivePath)
        {
            byte[] bytes = ReadZipEntry(archivePath, "manifest.json");
            return JsonSerializer.Deserialize<DatabaseBackupManifest>(bytes, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
            }) ?? throw new InvalidOperationException("backup manifest is unreadable");
        }

        private static byte[] ReadZipEntry(string archivePath, string entryName)
        {
            using (ZipArchive archive = ZipFile.OpenRead(archivePath))
            using (Stream stream = archive.GetEntry(entryName)?.Open()
                ?? throw new InvalidOperationException("backup entry is missing: " + entryName))
            using (MemoryStream buffer = new MemoryStream())
            {
                stream.CopyTo(buffer);
                return buffer.ToArray();
            }
        }

        private static async Task<int> ReadSqliteSchemaVersionAsync(string databasePath)
        {
            using (SqliteConnection connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString()))
            {
                await connection.OpenAsync().ConfigureAwait(false);
                using (SqliteCommand command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_migrations;";
                    return Convert.ToInt32(await command.ExecuteScalarAsync().ConfigureAwait(false));
                }
            }
        }

        private static async Task<bool> SqliteHasFleetAsync(string databasePath, string name)
        {
            using (SqliteConnection connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString()))
            {
                await connection.OpenAsync().ConfigureAwait(false);
                using (SqliteCommand command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT COUNT(*) FROM fleets WHERE name = @name;";
                    command.Parameters.AddWithValue("@name", name);
                    return Convert.ToInt32(await command.ExecuteScalarAsync().ConfigureAwait(false)) > 0;
                }
            }
        }

        private static ArmadaServer NewServer(string tempDir, bool harborEnabled = false)
        {
            DatabaseSettings dbSettings = new DatabaseSettings
            {
                Type = DatabaseTypeEnum.Sqlite,
                Filename = Path.Combine(tempDir, "armada.db")
            };
            ArmadaSettings settings = new ArmadaSettings
            {
                DataDirectory = tempDir,
                DatabasePath = dbSettings.Filename,
                Database = dbSettings,
                LogDirectory = Path.Combine(tempDir, "logs"),
                DocksDirectory = Path.Combine(tempDir, "docks"),
                ReposDirectory = Path.Combine(tempDir, "repos"),
                AdmiralPort = FreePort(),
                McpPort = FreePort(),
                ApiKey = "test-key-" + Guid.NewGuid().ToString("N"),
                HeartbeatIntervalSeconds = 300
            };
            settings.Rest.Hostname = "127.0.0.1";
            settings.AutonomousObjectiveScheduler.Enabled = false;
            settings.Harbor.Enabled = harborEnabled;
            settings.SettingsFilePath = Path.Combine(tempDir, "settings.json");
            settings.InitializeDirectories();

            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return new ArmadaServer(logging, settings, quiet: true);
        }

        private static bool IsDatabaseDisposed(ArmadaServer server)
        {
            DatabaseDriver database = GetField<DatabaseDriver>(server, "_Database");
            FieldInfo? disposed = database.GetType().GetField("_Disposed", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("the database driver must track disposal");
            return (bool)disposed.GetValue(database)!;
        }

        private static T GetField<T>(ArmadaServer server, string name)
        {
            FieldInfo field = typeof(ArmadaServer).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("ArmadaServer must have the field " + name);
            return (T)field.GetValue(server)!;
        }

        private static void SetField(ArmadaServer server, string name, object value)
        {
            FieldInfo field = typeof(ArmadaServer).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("ArmadaServer must have the field " + name);
            field.SetValue(server, value);
        }

        private static string NewTempDirectory()
        {
            string path = Path.Combine(Path.GetTempPath(), "armada_server_stop_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        private static void DeleteDirectory(string path)
        {
            try
            {
                Directory.Delete(path, true);
            }
            catch (IOException)
            {
                // A stopped server can briefly hold log handles; the temporary directory is disposable.
            }
            catch (UnauthorizedAccessException)
            {
                // Same as above on hosts that report a held handle as an access failure.
            }
        }

        private static int FreePort()
        {
            using (TcpListener listener = new TcpListener(IPAddress.Loopback, 0))
            {
                listener.Start();
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                listener.Stop();
                return port;
            }
        }

        #endregion
    }
}
