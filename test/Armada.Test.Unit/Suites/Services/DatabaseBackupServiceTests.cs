namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.IO.Compression;
    using System.Security.Cryptography;
    using System.Text.Json;
    using System.Threading;
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
    /// Provider-aware backup and restore used by the MCP, REST and WebSocket entry points.
    /// </summary>
    public sealed class DatabaseBackupServiceTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Database Backup Service";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("NonSqliteProvider_Backup_NeverReportsSnapshotOfUnusedSqliteFile", async () =>
            {
                if (SkipWindows("NonSqliteProvider_Backup_NeverReportsSnapshotOfUnusedSqliteFile")) return;
                using (SelfDeployTestDirectory directory = new SelfDeployTestDirectory())
                {
                    string unusedSqlite = CreateSqliteFile(directory.Root, "unused-stub.db", migrated: true);
                    ArmadaSettings settings = PostgresqlSettings(directory.Root, unusedSqlite);
                    string outputPath = Path.Combine(directory.Root, "out", "backup.zip");

                    string failure = await BackupFailureAsync(settings, outputPath);

                    AssertTrue(failure.StartsWith("postgresql_", StringComparison.Ordinal), "backup on PostgreSQL fails with a native PostgreSQL reason: " + failure);
                    bool snapshotWritten = File.Exists(outputPath) && ZipContains(outputPath, "armada.db");
                    AssertFalse(snapshotWritten, "no SQLite snapshot of the unused file may be produced");
                }
            });

            await RunTest("NonSqliteProvider_Restore_NeverOverwritesUnusedSqliteFile", async () =>
            {
                if (SkipWindows("NonSqliteProvider_Restore_NeverOverwritesUnusedSqliteFile")) return;
                using (SelfDeployTestDirectory directory = new SelfDeployTestDirectory())
                {
                    string unusedSqlite = CreateSqliteFile(directory.Root, "unused-stub.db", migrated: true);
                    string hashBefore = Sha256(unusedSqlite);
                    ArmadaSettings settings = PostgresqlSettings(directory.Root, unusedSqlite);
                    string uploaded = CreateSqliteBackupZip(directory.Root, "uploaded.zip");

                    string failure = await RestoreFailureAsync(settings, uploaded);

                    AssertEqual("restore_unsupported_for_provider_Postgresql", failure, "restore refusal reason");
                    AssertEqual(hashBefore, Sha256(unusedSqlite), "the unused SQLite file is unchanged");
                }
            });

            await RunTest("Sqlite_Backup_WritesVerifiedArchiveWithProviderManifest", async () =>
            {
                if (SkipWindows("Sqlite_Backup_WritesVerifiedArchiveWithProviderManifest")) return;
                using (SelfDeployTestDirectory directory = new SelfDeployTestDirectory())
                {
                    ArmadaSettings settings = SqliteSettings(directory.Root);
                    int schemaVersion;
                    using (DatabaseDriver driver = await DatabaseDriverFactory.CreateAndInitializeAsync(settings.Database))
                    {
                        await driver.Fleets.CreateAsync(new Fleet("backup-fleet"));
                        schemaVersion = await driver.GetSchemaVersionAsync();
                    }
                    string outputPath = Path.Combine(directory.Root, "out", "backup.zip");

                    DatabaseBackupResult result = await BackupAsync(settings, outputPath);

                    AssertEqual(outputPath, result.Path, "archive path");
                    AssertTrue(ZipContains(outputPath, "armada.db"), "SQLite archive keeps the restorable database entry");
                    DatabaseBackupManifest manifest = ReadManifest(outputPath);
                    AssertEqual(DatabaseTypeEnum.Sqlite, manifest.DatabaseType, "manifest provider");
                    AssertEqual(schemaVersion, manifest.SchemaVersion, "manifest schema version from the provider");
                    AssertEqual(1L, manifest.RecordCounts["fleets"], "manifest record count from the provider");
                    AssertTrue(manifest.BackupValidated, "native backup validated");
                    AssertTrue(manifest.RestoreVerified, "isolated restore verified");
                    AssertEqual(Sha256Bytes(ZipEntryBytes(outputPath, manifest.ArtifactEntry)), manifest.ArtifactSha256, "artifact digest matches the archive");
                }
            });

            await RunTest("Restore_ArchiveFromAnotherProvider_IsRefusedWithoutChanges", async () =>
            {
                if (SkipWindows("Restore_ArchiveFromAnotherProvider_IsRefusedWithoutChanges")) return;
                using (SelfDeployTestDirectory directory = new SelfDeployTestDirectory())
                {
                    ArmadaSettings settings = SqliteSettings(directory.Root);
                    using (DatabaseDriver driver = await DatabaseDriverFactory.CreateAndInitializeAsync(settings.Database)) { }
                    string hashBefore = Sha256(settings.Database.Filename);
                    string foreign = Path.Combine(directory.Root, "postgresql-backup.zip");
                    using (ZipArchive zip = ZipFile.Open(foreign, ZipArchiveMode.Create))
                    {
                        WriteEntry(zip, "database/armada-backup.dump", "not sqlite");
                        WriteEntry(zip, "manifest.json", "{\"databaseType\":\"Postgresql\",\"artifactEntry\":\"database/armada-backup.dump\"}");
                    }

                    string failure = await RestoreFailureAsync(settings, foreign);

                    AssertEqual("backup_provider_mismatch", failure, "cross-provider restore refused");
                    AssertEqual(hashBefore, Sha256(settings.Database.Filename), "configured database unchanged");
                }
            });

            await RunTest("Sqlite_Restore_ReplacesConfiguredDatabaseAfterSafetyBackup", async () =>
            {
                if (SkipWindows("Sqlite_Restore_ReplacesConfiguredDatabaseAfterSafetyBackup")) return;
                using (SelfDeployTestDirectory directory = new SelfDeployTestDirectory())
                {
                    ArmadaSettings settings = SqliteSettings(directory.Root);
                    using (DatabaseDriver driver = await DatabaseDriverFactory.CreateAndInitializeAsync(settings.Database))
                    {
                        await driver.Fleets.CreateAsync(new Fleet("before-restore"));
                    }
                    string archive = Path.Combine(directory.Root, "out", "baseline.zip");
                    await BackupAsync(settings, archive);
                    using (DatabaseDriver driver = await DatabaseDriverFactory.CreateAndInitializeAsync(settings.Database))
                    {
                        await driver.Fleets.CreateAsync(new Fleet("after-backup"));
                    }

                    DatabaseRestoreResult restored = await RestoreAsync(settings, archive);

                    AssertEqual("restored", restored.Status, "restore status");
                    AssertTrue(File.Exists(restored.SafetyBackupPath), "safety backup written before replacement");
                    using (DatabaseDriver driver = await DatabaseDriverFactory.CreateAndInitializeAsync(settings.Database))
                    {
                        List<Fleet> fleets = await driver.Fleets.EnumerateAsync();
                        AssertEqual(1, fleets.Count, "configured database holds the backed-up rows only");
                        AssertEqual("before-restore", fleets[0].Name, "restored row");
                    }
                }
            });

            await RunSettingsRedactionTestsAsync();
        }

        private const string ArchiveSecretSettings = @"{
  ""admiralPort"": 7890,
  ""apiKey"": ""armada-api-key-0123456789"",
  ""gitHubToken"": ""ghp_abcdefghijklmnopqrstuvwxyz0123"",
  ""sessionTokenEncryptionKey"": ""session-encryption-key-987654"",
  ""database"": { ""type"": ""Postgresql"", ""hostname"": ""db"", ""password"": ""pg-password-secret-42"", ""connectionString"": ""Host=db;Username=armada;Password=conn-string-secret-77;"" },
  ""agents"": [ { ""environment"": { ""ANTHROPIC_API_KEY"": ""sk-ant-api03-secretsecretsecret"", ""LOG_LEVEL"": ""debug"" } } ],
  ""remoteTrigger"": { ""drainerBearerToken"": ""bearer-token-secret-55"" },
  ""modelProviders"": { ""providers"": { ""vilao"": { ""apiKeyEnv"": ""ARMADA_VILAO_KEY"" } } }
}";

        private static readonly string[] ArchiveSecretValues =
        {
            "armada-api-key-0123456789", "ghp_abcdefghijklmnopqrstuvwxyz0123", "session-encryption-key-987654",
            "pg-password-secret-42", "conn-string-secret-77", "sk-ant-api03-secretsecretsecret", "bearer-token-secret-55"
        };

        private async Task RunSettingsRedactionTestsAsync()
        {
            await RunTest("Backup_ArchiveSettingsJson_ContainsNoSecretValues", async () =>
            {
                using (SelfDeployTestDirectory directory = new SelfDeployTestDirectory())
                using (SettingsFileScope settingsFile = new SettingsFileScope(ArchiveSecretSettings))
                {
                    ArmadaSettings settings = SqliteSettings(directory.Root);
                    using (DatabaseDriver driver = await DatabaseDriverFactory.CreateAndInitializeAsync(settings.Database)) { }
                    string archive = Path.Combine(directory.Root, "out", "backup.zip");

                    await BackupAsync(settings, archive);

                    string archived = System.Text.Encoding.UTF8.GetString(ZipEntryBytes(archive, "settings.json"));
                    foreach (string secret in ArchiveSecretValues)
                        AssertFalse(archived.Contains(secret, StringComparison.Ordinal), "archive settings must not contain " + secret.Substring(0, 6) + "...");
                    AssertContains("7890", archived, "non-secret setting kept");
                    AssertContains("ARMADA_VILAO_KEY", archived, "an environment variable name is not a secret");
                    AssertContains("debug", archived, "non-secret environment value kept");
                    DatabaseBackupManifest manifest = ReadManifest(archive);
                    AssertTrue(manifest.SettingsRedacted, "manifest records the redaction");
                    AssertEqual(ArchiveSecretValues.Length, manifest.RedactedSettingCount, "every secret value counted");
                }
            });

            await RunTest("Restore_RedactedArchive_KeepsTargetSecretsAndNeverWritesPlaceholder", async () =>
            {
                using (SelfDeployTestDirectory directory = new SelfDeployTestDirectory())
                using (SettingsFileScope settingsFile = new SettingsFileScope(ArchiveSecretSettings))
                {
                    ArmadaSettings settings = SqliteSettings(directory.Root);
                    using (DatabaseDriver driver = await DatabaseDriverFactory.CreateAndInitializeAsync(settings.Database)) { }
                    string archive = Path.Combine(directory.Root, "out", "backup.zip");
                    await BackupAsync(settings, archive);

                    File.WriteAllText(ArmadaSettings.DefaultSettingsPath, @"{
  ""admiralPort"": 9999,
  ""apiKey"": ""target-api-key-live"",
  ""gitHubToken"": ""ghp_targettargettargettarget0000"",
  ""sessionTokenEncryptionKey"": ""target-session-key"",
  ""database"": { ""type"": ""Postgresql"", ""hostname"": ""db"", ""password"": ""target-pg-password"", ""connectionString"": ""Host=db;Password=target-conn;"" },
  ""agents"": [ { ""environment"": { ""ANTHROPIC_API_KEY"": ""sk-ant-target-live-key"" } } ]
}");

                    await RestoreAsync(settings, archive);

                    string restored = File.ReadAllText(ArmadaSettings.DefaultSettingsPath);
                    AssertFalse(restored.Contains("[REDACTED", StringComparison.Ordinal), "no placeholder written");
                    AssertContains("7890", restored, "non-secret value restored from the archive");
                    AssertFalse(restored.Contains("9999", StringComparison.Ordinal), "non-secret value replaced by the archive");
                    foreach (string kept in new[] { "target-api-key-live", "ghp_targettargettargettarget0000", "target-session-key", "target-pg-password", "Host=db;Password=target-conn;", "sk-ant-target-live-key" })
                        AssertContains(kept, restored, "target secret kept");
                    foreach (string secret in ArchiveSecretValues)
                        AssertFalse(restored.Contains(secret, StringComparison.Ordinal), "archive secret never restored");
                    AssertFalse(restored.Contains("drainerBearerToken", StringComparison.Ordinal), "a redacted setting the target lacks is dropped");
                }
            });
        }

        // Writes the default settings file for one test and restores whatever was there before. The unit test process
        // redirects the data directory, so this never touches a real settings file.
        private sealed class SettingsFileScope : IDisposable
        {
            private readonly string? _Previous;

            public SettingsFileScope(string content)
            {
                string path = ArmadaSettings.DefaultSettingsPath;
                _Previous = File.Exists(path) ? File.ReadAllText(path) : null;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, content);
            }

            public void Dispose()
            {
                string path = ArmadaSettings.DefaultSettingsPath;
                if (_Previous == null)
                {
                    if (File.Exists(path)) File.Delete(path);
                }
                else
                {
                    File.WriteAllText(path, _Previous);
                }
            }
        }

        private bool SkipWindows(string testName)
        {
            return false;
        }

        private static ArmadaSettings PostgresqlSettings(string root, string unusedSqlite)
        {
            ArmadaSettings settings = new ArmadaSettings();
            settings.DataDirectory = Path.Combine(root, "data");
            settings.DatabasePath = unusedSqlite;
            settings.Database = new DatabaseSettings
            {
                Type = DatabaseTypeEnum.Postgresql,
                Hostname = "127.0.0.1",
                Port = 1,
                DatabaseName = "armada_unreachable",
                Username = "armada",
                Password = "unused-test-password"
            };
            return settings;
        }

        private static ArmadaSettings SqliteSettings(string root)
        {
            ArmadaSettings settings = new ArmadaSettings();
            settings.DataDirectory = Path.Combine(root, "data");
            Directory.CreateDirectory(settings.DataDirectory);
            string file = Path.Combine(settings.DataDirectory, "armada.db");
            settings.DatabasePath = file;
            settings.Database = new DatabaseSettings { Type = DatabaseTypeEnum.Sqlite, Filename = file };
            return settings;
        }

        private static string CreateSqliteFile(string root, string name, bool migrated)
        {
            string path = Path.Combine(root, name);
            using (SqliteConnection connection = new SqliteConnection("Data Source=" + path + ";Pooling=False"))
            {
                connection.Open();
                using (SqliteCommand command = connection.CreateCommand())
                {
                    command.CommandText = migrated
                        ? "CREATE TABLE schema_migrations (version INTEGER NOT NULL); INSERT INTO schema_migrations VALUES (1); CREATE TABLE fleets (id TEXT);"
                        : "CREATE TABLE unrelated (id INTEGER);";
                    command.ExecuteNonQuery();
                }
            }
            return path;
        }

        private static string CreateSqliteBackupZip(string root, string name)
        {
            string database = CreateSqliteFile(root, "archive-source-" + Guid.NewGuid().ToString("N") + ".db", migrated: true);
            string zipPath = Path.Combine(root, name);
            using (ZipArchive zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                zip.CreateEntryFromFile(database, "armada.db");
            }
            return zipPath;
        }

        private static void WriteEntry(ZipArchive zip, string name, string content)
        {
            ZipArchiveEntry entry = zip.CreateEntry(name);
            using (StreamWriter writer = new StreamWriter(entry.Open()))
            {
                writer.Write(content);
            }
        }

        private static bool ZipContains(string zipPath, string entryName)
        {
            using (ZipArchive zip = ZipFile.OpenRead(zipPath))
            {
                return zip.GetEntry(entryName) != null;
            }
        }

        private static byte[] ZipEntryBytes(string zipPath, string entryName)
        {
            using (ZipArchive zip = ZipFile.OpenRead(zipPath))
            using (Stream stream = zip.GetEntry(entryName)!.Open())
            using (MemoryStream buffer = new MemoryStream())
            {
                stream.CopyTo(buffer);
                return buffer.ToArray();
            }
        }

        private static DatabaseBackupManifest ReadManifest(string zipPath)
        {
            byte[] bytes = ZipEntryBytes(zipPath, "manifest.json");
            return JsonSerializer.Deserialize<DatabaseBackupManifest>(bytes, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
            }) ?? throw new InvalidOperationException("manifest unreadable");
        }

        private static string Sha256(string path)
        {
            return Sha256Bytes(File.ReadAllBytes(path));
        }

        private static string Sha256Bytes(byte[] bytes)
        {
            return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        }

        // The adapters below are the only place these tests reach production code: the shared service that the
        // MCP, REST and WebSocket handlers call.

        private static async Task<DatabaseBackupResult> BackupAsync(ArmadaSettings settings, string outputPath)
        {
            using (DatabaseDriver driver = await DatabaseDriverFactory.CreateAndInitializeAsync(settings.Database))
            {
                return await new DatabaseBackupService(driver, settings).BackupAsync(outputPath);
            }
        }

        private static async Task<string> BackupFailureAsync(ArmadaSettings settings, string outputPath)
        {
            using (DatabaseDriver sidecar = await CreateSidecarDriverAsync(settings))
            {
                try
                {
                    await new DatabaseBackupService(sidecar, settings, new FailingNativeRunner()).BackupAsync(outputPath);
                    return String.Empty;
                }
                catch (DatabaseBackupException ex)
                {
                    return ex.FailureReason;
                }
            }
        }

        private static async Task<DatabaseRestoreResult> RestoreAsync(ArmadaSettings settings, string archive)
        {
            using (DatabaseDriver driver = await DatabaseDriverFactory.CreateAndInitializeAsync(settings.Database))
            {
                return await new DatabaseBackupService(driver, settings).RestoreAsync(archive);
            }
        }

        private static async Task<string> RestoreFailureAsync(ArmadaSettings settings, string archive)
        {
            DatabaseSettings driverSettings = settings.Database.Type == DatabaseTypeEnum.Sqlite ? settings.Database : SidecarSettings(settings);
            using (DatabaseDriver driver = await DatabaseDriverFactory.CreateAndInitializeAsync(driverSettings))
            {
                try
                {
                    await new DatabaseBackupService(driver, settings).RestoreAsync(archive);
                    return String.Empty;
                }
                catch (DatabaseBackupException ex)
                {
                    return ex.FailureReason;
                }
            }
        }

        // A driver for an unrelated disposable SQLite file, used only where the configured provider is unreachable:
        // the service must fail or refuse before it would use the driver.
        private static Task<DatabaseDriver> CreateSidecarDriverAsync(ArmadaSettings settings)
        {
            return DatabaseDriverFactory.CreateAndInitializeAsync(SidecarSettings(settings));
        }

        private static DatabaseSettings SidecarSettings(ArmadaSettings settings)
        {
            Directory.CreateDirectory(settings.DataDirectory);
            return new DatabaseSettings { Type = DatabaseTypeEnum.Sqlite, Filename = Path.Combine(settings.DataDirectory, "sidecar-" + Guid.NewGuid().ToString("N") + ".db") };
        }

        private sealed class FailingNativeRunner : ISelfDeployNativeCommandRunner
        {
            public Task<SelfDeployNativeCommandResult> RunAsync(SelfDeployNativeCommandRequest request, CancellationToken token = default)
            {
                return Task.FromResult(new SelfDeployNativeCommandResult { ExitCode = 1, StandardError = "connection refused" });
            }
        }
    }
}
