namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.IO;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using Microsoft.Data.Sqlite;

    /// <summary>
    /// A native database client or runtime that is not installed is reported by name through every consumer.
    /// </summary>
    public sealed class SelfDeployNativeClientTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Native Client Availability";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("BackupProvider_PostgresqlWithoutPgDump_ReportsMissingClient", async () =>
            {
                if (SkipWindows("BackupProvider_PostgresqlWithoutPgDump_ReportsMissingClient")) return;
                using (SelfDeployTestDirectory directory = new SelfDeployTestDirectory())
                using (PathScope path = PathScope.Empty(directory.Root))
                {
                    SelfDeployDatabaseBackupProvider provider = new SelfDeployDatabaseBackupProvider(
                        ServerSettings(DatabaseTypeEnum.Postgresql), Path.Combine(directory.Root, "backups"), new SelfDeployNativeCommandRunner());
                    SelfDeployDatabaseBackupResult result = await provider.CreateAndVerifyAsync();
                    AssertFalse(result.BackupValidated, "no backup without the client");
                    AssertEqual("native_client_missing_pg_dump", result.FailureReason, "missing client named");
                }
            });

            await RunTest("BackupProvider_MysqlWithoutMysqlClient_ReportsMissingClient", async () =>
            {
                if (SkipWindows("BackupProvider_MysqlWithoutMysqlClient_ReportsMissingClient")) return;
                using (SelfDeployTestDirectory directory = new SelfDeployTestDirectory())
                using (PathScope path = PathScope.Empty(directory.Root))
                {
                    SelfDeployDatabaseBackupProvider provider = new SelfDeployDatabaseBackupProvider(
                        ServerSettings(DatabaseTypeEnum.Mysql), Path.Combine(directory.Root, "backups"), new SelfDeployNativeCommandRunner());
                    SelfDeployDatabaseBackupResult result = await provider.CreateAndVerifyAsync();
                    AssertEqual("native_client_missing_mysql", result.FailureReason, "missing client named");
                }
            });

            await RunTest("BackupService_PostgresqlWithoutPgDump_RefusesWithMissingClientAndNoArchive", async () =>
            {
                if (SkipWindows("BackupService_PostgresqlWithoutPgDump_RefusesWithMissingClientAndNoArchive")) return;
                using (SelfDeployTestDirectory directory = new SelfDeployTestDirectory())
                {
                    ArmadaSettings settings = new ArmadaSettings { DataDirectory = Path.Combine(directory.Root, "data") };
                    settings.Database = ServerSettings(DatabaseTypeEnum.Postgresql);
                    string archive = Path.Combine(directory.Root, "out", "backup.zip");
                    Directory.CreateDirectory(settings.DataDirectory);
                    using (DatabaseDriver sidecar = await DatabaseDriverFactory.CreateAndInitializeAsync(new DatabaseSettings
                    {
                        Type = DatabaseTypeEnum.Sqlite,
                        Filename = Path.Combine(settings.DataDirectory, "sidecar.db")
                    }))
                    using (PathScope path = PathScope.Empty(directory.Root))
                    {
                        string reason = String.Empty;
                        try
                        {
                            await new DatabaseBackupService(sidecar, settings).BackupAsync(archive);
                        }
                        catch (DatabaseBackupException ex)
                        {
                            reason = ex.FailureReason;
                        }
                        AssertEqual("native_client_missing_pg_dump", reason, "backup reports the missing client");
                        AssertFalse(File.Exists(archive), "no archive written");
                    }
                }
            });

            await RunTest("DefaultPreflight_PostgresqlWithoutPgDump_RefusesCutoverWithMissingClient", async () =>
            {
                if (SkipWindows("DefaultPreflight_PostgresqlWithoutPgDump_RefusesCutoverWithMissingClient")) return;
                using (SelfDeployTestDirectory directory = new SelfDeployTestDirectory())
                using (PathScope path = PathScope.Empty(directory.Root))
                {
                    ArmadaSettings settings = new ArmadaSettings { DataDirectory = Path.Combine(directory.Root, "data") };
                    settings.Database = ServerSettings(DatabaseTypeEnum.Postgresql);
                    SelfDeployPreflightResult result = await SelfDeployNativePreflight.CreateDefault(settings).ValidateAsync(new SelfDeployPreflightRequest
                    {
                        WorkingDirectory = directory.Root,
                        CandidateServerDllPath = Path.Combine(directory.Root, "Armada.Server.dll")
                    });
                    AssertFalse(result.IsSafeToCutover, "cutover refused");
                    AssertEqual("native_client_missing_pg_dump", result.FailureReason, "preflight reports the missing client");
                }
            });

            await RunTest("CandidateValidator_WithoutDotnet_ReportsMissingRuntime", async () =>
            {
                if (SkipWindows("CandidateValidator_WithoutDotnet_ReportsMissingRuntime")) return;
                using (SelfDeployTestDirectory directory = new SelfDeployTestDirectory())
                {
                    DatabaseSettings sqlite = new DatabaseSettings { Type = DatabaseTypeEnum.Sqlite, Filename = CreateMigratedSqlite(directory.Root) };
                    SelfDeployDatabaseBackupProvider provider = new SelfDeployDatabaseBackupProvider(sqlite, Path.Combine(directory.Root, "backups"), new SelfDeployNativeCommandRunner());
                    SelfDeployDatabaseBackupResult backup = await provider.CreateAndVerifyAsync();
                    AssertTrue(backup.RestoreVerified, "SQLite backup needs no external client: " + backup.FailureReason);
                    string candidate = Path.Combine(directory.Root, "Armada.Server.dll");
                    File.WriteAllText(candidate, "candidate");
                    try
                    {
                        SelfDeployCandidateValidationResult result;
                        using (PathScope path = PathScope.Empty(directory.Root))
                        {
                            result = await new SelfDeployCandidateProcessValidator(provider, new SelfDeployNativeCommandRunner(), sqlite)
                                .ValidateAsync(new SelfDeployPreflightRequest { CandidateServerDllPath = candidate, WorkingDirectory = directory.Root }, backup);
                        }
                        AssertEqual("native_client_missing_dotnet", result.FailureReason, "validator reports the missing runtime");
                    }
                    finally
                    {
                        await provider.CleanupAsync(backup);
                    }
                }
            });
        }

        private bool SkipWindows(string testName)
        {
            return false;
        }

        private static DatabaseSettings ServerSettings(DatabaseTypeEnum type)
        {
            return new DatabaseSettings
            {
                Type = type,
                Hostname = "127.0.0.1",
                Port = 1,
                DatabaseName = "armada_client_probe",
                Username = "armada",
                Password = "unused-test-password"
            };
        }

        private static string CreateMigratedSqlite(string root)
        {
            string path = Path.Combine(root, "source.db");
            using (SqliteConnection connection = new SqliteConnection("Data Source=" + path + ";Pooling=False"))
            {
                connection.Open();
                using (SqliteCommand command = connection.CreateCommand())
                {
                    command.CommandText = "CREATE TABLE schema_migrations (version INTEGER NOT NULL);";
                    command.ExecuteNonQuery();
                }
            }
            return path;
        }

        // Replaces PATH with an empty directory so an executable lookup fails the same way it does on a host
        // without the client installed, and restores it on dispose.
        private sealed class PathScope : IDisposable
        {
            private readonly string? _Previous;

            private PathScope(string emptyDirectory)
            {
                _Previous = Environment.GetEnvironmentVariable("PATH");
                Directory.CreateDirectory(emptyDirectory);
                Environment.SetEnvironmentVariable("PATH", emptyDirectory);
            }

            public static PathScope Empty(string root)
            {
                return new PathScope(Path.Combine(root, "empty-path"));
            }

            public void Dispose()
            {
                Environment.SetEnvironmentVariable("PATH", _Previous);
            }
        }
    }
}
