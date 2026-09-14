namespace Armada.Core.Services
{
    using System;
    using System.IO;
    using System.IO.Compression;
    using System.Security.Cryptography;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Microsoft.Data.Sqlite;

    /// <summary>
    /// The single backup and restore path for every entry point. A backup is a verified provider-native backup of
    /// the configured database: created with the provider's own utilities, restored into an owned isolated target
    /// and checked, then archived with a manifest whose schema version and record counts come from that provider.
    /// Restore replaces the database only for SQLite deployments; a server provider is refused before anything is
    /// read or changed, because an in-place native restore of a live server database cannot be made atomic.
    /// </summary>
    public sealed class DatabaseBackupService
    {
        /// <summary>
        /// Archive entry name of a SQLite database.
        /// </summary>
        public const string SqliteEntry = "armada.db";

        private static readonly JsonSerializerOptions ManifestOptions = CreateManifestOptions();
        private readonly DatabaseDriver _Database;
        private readonly ArmadaSettings _Settings;
        private readonly ISelfDeployNativeCommandRunner? _Commands;

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="database">Driver of the configured database.</param>
        /// <param name="settings">Armada settings.</param>
        /// <param name="commands">Optional native command runner.</param>
        public DatabaseBackupService(DatabaseDriver database, ArmadaSettings settings, ISelfDeployNativeCommandRunner? commands = null)
        {
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _Commands = commands;
        }

        /// <summary>
        /// Create a verified native backup archive.
        /// </summary>
        /// <param name="outputPath">Archive path, or null for a timestamped file under the data directory.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Backup result.</returns>
        /// <exception cref="DatabaseBackupException">The backup could not be created and verified; no archive is left.</exception>
        public async Task<DatabaseBackupResult> BackupAsync(string? outputPath, CancellationToken token = default)
        {
            DatabaseSettings database = _Settings.Database;
            string backupsDirectory = Path.Combine(_Settings.DataDirectory, "backups");
            string timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd-HHmmss");
            string archivePath = Path.GetFullPath(outputPath ?? Path.Combine(backupsDirectory, "armada-backup-" + timestamp + ".zip"));
            string nativeDirectory = Path.Combine(backupsDirectory, ".native-" + Guid.NewGuid().ToString("N"));

            SelfDeployDatabaseBackupProvider provider = new SelfDeployDatabaseBackupProvider(
                database,
                nativeDirectory,
                _Commands,
                _Settings.SelfDeploy.SqlServerBackupDirectory);

            try
            {
                SelfDeployDatabaseBackupResult native = await provider.CreateAndVerifyAsync(token).ConfigureAwait(false);
                bool cleaned = !provider.OwnsTarget(native) || await provider.CleanupAsync(native, CancellationToken.None).ConfigureAwait(false);
                if (!native.BackupValidated || !native.RestoreVerified)
                    throw new DatabaseBackupException(String.IsNullOrWhiteSpace(native.FailureReason) ? "backup_not_verified" : native.FailureReason, false);
                if (!cleaned) throw new DatabaseBackupException("backup_isolated_cleanup_failed", false);

                int schemaVersion = await _Database.GetSchemaVersionAsync(token).ConfigureAwait(false);
                System.Collections.Generic.Dictionary<string, long> counts = await DatabaseRecordCounter.CountAsync(
                    database, DatabaseRecordCounter.ManifestTables, token).ConfigureAwait(false);

                DatabaseBackupManifest manifest = new DatabaseBackupManifest
                {
                    BackupTimestampUtc = DateTime.UtcNow.ToString("o"),
                    DatabaseType = database.Type,
                    SchemaVersion = schemaVersion,
                    ArmadaVersion = Constants.ProductVersion,
                    RecordCounts = counts,
                    BackupValidated = true,
                    RestoreVerified = true
                };

                bool artifactIsLocal = File.Exists(native.ArtifactPath);
                if (artifactIsLocal)
                {
                    manifest.ArtifactEntry = ArtifactEntryName(database.Type);
                    manifest.ArtifactSha256 = await Sha256FileAsync(native.ArtifactPath, token).ConfigureAwait(false);
                }
                else
                {
                    // SQL Server writes the artifact on the database host; the archive records where it is.
                    manifest.ServerArtifactPath = native.ArtifactPath;
                }

                string? redactedSettings = null;
                if (File.Exists(ArmadaSettings.DefaultSettingsPath))
                {
                    try
                    {
                        string liveSettings = await File.ReadAllTextAsync(ArmadaSettings.DefaultSettingsPath, token).ConfigureAwait(false);
                        redactedSettings = SettingsSecretRedaction.Redact(liveSettings, out int redactedCount);
                        manifest.SettingsRedacted = true;
                        manifest.RedactedSettingCount = redactedCount;
                    }
                    catch (JsonException ex)
                    {
                        throw new DatabaseBackupException("backup_settings_unreadable", false, ex);
                    }
                }

                await WriteArchiveAsync(archivePath, manifest, artifactIsLocal ? native.ArtifactPath : null, redactedSettings, token).ConfigureAwait(false);

                return new DatabaseBackupResult
                {
                    Path = archivePath,
                    TimestampUtc = manifest.BackupTimestampUtc,
                    DatabaseType = manifest.DatabaseType,
                    SchemaVersion = schemaVersion,
                    SizeBytes = new FileInfo(archivePath).Length,
                    RecordCounts = counts,
                    ServerArtifactPath = manifest.ServerArtifactPath
                };
            }
            catch (DatabaseBackupException)
            {
                throw;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException || ex is System.Data.Common.DbException)
            {
                throw new DatabaseBackupException("backup_archive_failed", false, ex);
            }
            finally
            {
                DeleteDirectoryQuietly(nativeDirectory);
            }
        }

        /// <summary>
        /// Restore a backup archive into the configured SQLite database after a verified safety backup.
        /// </summary>
        /// <param name="archivePath">Backup archive.</param>
        /// <param name="originalFilename">Display name for the operator message.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Restore result.</returns>
        /// <exception cref="DatabaseBackupException">Refused, or failed before the database was replaced.</exception>
        public async Task<DatabaseRestoreResult> RestoreAsync(string archivePath, string? originalFilename = null, CancellationToken token = default)
        {
            DatabaseSettings database = _Settings.Database;
            if (database.Type != DatabaseTypeEnum.Sqlite)
                throw new DatabaseBackupException("restore_unsupported_for_provider_" + database.Type, true);
            if (String.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath))
                throw new DatabaseBackupException("backup_archive_missing", true);

            string backupsDirectory = Path.Combine(_Settings.DataDirectory, "backups");
            string workDirectory = Path.Combine(backupsDirectory, ".restore-" + Guid.NewGuid().ToString("N"));
            try
            {
                SelfDeployPrivateFile.CreateDirectory(workDirectory);
                string extractedDatabase = Path.Combine(workDirectory, SqliteEntry);
                string extractedSettings = Path.Combine(workDirectory, "settings.json");
                int archivedSchemaVersion = 0;
                bool hasSettings = false;

                using (ZipArchive zip = ZipFile.OpenRead(archivePath))
                {
                    ZipArchiveEntry? manifestEntry = zip.GetEntry("manifest.json");
                    if (manifestEntry != null)
                    {
                        ArchivedManifest archived = await ReadManifestAsync(manifestEntry, token).ConfigureAwait(false);
                        if (archived.DatabaseType.HasValue && archived.DatabaseType.Value != DatabaseTypeEnum.Sqlite)
                            throw new DatabaseBackupException("backup_provider_mismatch", true);
                        archivedSchemaVersion = archived.SchemaVersion;
                    }

                    ZipArchiveEntry? databaseEntry = zip.GetEntry(SqliteEntry);
                    if (databaseEntry == null) throw new DatabaseBackupException("backup_database_entry_missing", true);
                    databaseEntry.ExtractToFile(extractedDatabase, false);
                    ZipArchiveEntry? settingsEntry = zip.GetEntry("settings.json");
                    if (settingsEntry != null)
                    {
                        settingsEntry.ExtractToFile(extractedSettings, false);
                        hasSettings = true;
                    }
                }

                if (!await IsValidArmadaSqliteAsync(extractedDatabase, token).ConfigureAwait(false))
                    throw new DatabaseBackupException("backup_database_invalid", true);

                // Merge settings before anything is replaced, so an unreadable document refuses the restore cleanly.
                string? mergedSettings = null;
                int preservedSecrets = 0;
                int droppedSecrets = 0;
                if (hasSettings)
                {
                    string archivedSettings = await File.ReadAllTextAsync(extractedSettings, token).ConfigureAwait(false);
                    string? targetSettings = File.Exists(ArmadaSettings.DefaultSettingsPath)
                        ? await File.ReadAllTextAsync(ArmadaSettings.DefaultSettingsPath, token).ConfigureAwait(false)
                        : null;
                    try
                    {
                        mergedSettings = SettingsSecretRedaction.MergeForRestore(archivedSettings, targetSettings, out preservedSecrets, out droppedSecrets);
                    }
                    catch (JsonException ex)
                    {
                        throw new DatabaseBackupException("restore_settings_unreadable", true, ex);
                    }
                }

                string safetyPath = Path.Combine(backupsDirectory, "pre-restore-" + DateTime.UtcNow.ToString("yyyy-MM-dd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".zip");
                DatabaseBackupResult safety = await BackupAsync(safetyPath, token).ConfigureAwait(false);

                // The online backup API writes into the live database through a connection, so open connections
                // held by the admiral stay valid; a file copy over an open database is unsafe and can fail.
                using (SqliteConnection source = new SqliteConnection("Data Source=" + extractedDatabase + ";Mode=ReadOnly;Pooling=False"))
                using (SqliteConnection target = new SqliteConnection("Data Source=" + database.Filename))
                {
                    await source.OpenAsync(token).ConfigureAwait(false);
                    await target.OpenAsync(token).ConfigureAwait(false);
                    source.BackupDatabase(target);
                }

                if (mergedSettings != null) await WriteSettingsAtomicallyAsync(mergedSettings, token).ConfigureAwait(false);

                string displayName = !String.IsNullOrEmpty(originalFilename) ? originalFilename : Path.GetFileName(archivePath);
                string message = "Database restored from " + displayName + ". ";
                if (!hasSettings) message += "Warning: settings.json was not found in the backup archive. ";
                else if (preservedSecrets > 0 || droppedSecrets > 0)
                    message += "Settings restored with " + preservedSecrets + " secret(s) kept from this host and " + droppedSecrets + " redacted value(s) without a local value omitted. ";
                message += "Restart the server to reload the restored data.";
                return new DatabaseRestoreResult
                {
                    Status = "restored",
                    SafetyBackupPath = safety.Path,
                    SchemaVersion = archivedSchemaVersion,
                    PreservedSecretCount = preservedSecrets,
                    DroppedSecretCount = droppedSecrets,
                    Message = message
                };
            }
            catch (DatabaseBackupException)
            {
                throw;
            }
            catch (SelfDeployPrivateStorageException ex)
            {
                throw new DatabaseBackupException(ex.FailureReason, false, ex);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidDataException || ex is SqliteException)
            {
                throw new DatabaseBackupException("restore_failed", false, ex);
            }
            finally
            {
                DeleteDirectoryQuietly(workDirectory);
            }
        }

        private static async Task WriteSettingsAtomicallyAsync(string settingsJson, CancellationToken token)
        {
            string path = ArmadaSettings.DefaultSettingsPath;
            string? directory = Path.GetDirectoryName(path);
            if (!String.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            string temporary = path + ".restore-" + Guid.NewGuid().ToString("N");
            try
            {
                await File.WriteAllTextAsync(temporary, settingsJson, token).ConfigureAwait(false);
                File.Move(temporary, path, true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }

        private static string ArtifactEntryName(DatabaseTypeEnum type)
        {
            switch (type)
            {
                case DatabaseTypeEnum.Sqlite: return SqliteEntry;
                case DatabaseTypeEnum.Postgresql: return "database/armada-backup.dump";
                case DatabaseTypeEnum.SqlServer: return "database/armada-backup.bak";
                default: return "database/armada-backup.sql";
            }
        }

        private static async Task WriteArchiveAsync(string archivePath, DatabaseBackupManifest manifest, string? artifactPath, string? redactedSettings, CancellationToken token)
        {
            string? directory = Path.GetDirectoryName(archivePath);
            if (!String.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            string partialPath = archivePath + ".partial-" + Guid.NewGuid().ToString("N");
            try
            {
                using (ZipArchive zip = ZipFile.Open(partialPath, ZipArchiveMode.Create))
                {
                    if (artifactPath != null) zip.CreateEntryFromFile(artifactPath, manifest.ArtifactEntry);
                    if (redactedSettings != null)
                    {
                        ZipArchiveEntry settingsEntry = zip.CreateEntry("settings.json");
                        using (StreamWriter writer = new StreamWriter(settingsEntry.Open()))
                        {
                            await writer.WriteAsync(redactedSettings.AsMemory(), token).ConfigureAwait(false);
                        }
                    }
                    ZipArchiveEntry manifestEntry = zip.CreateEntry("manifest.json");
                    using (Stream stream = manifestEntry.Open())
                    {
                        await JsonSerializer.SerializeAsync(stream, manifest, ManifestOptions, token).ConfigureAwait(false);
                    }
                }
                File.Move(partialPath, archivePath, true);
            }
            finally
            {
                if (File.Exists(partialPath)) File.Delete(partialPath);
            }
        }

        private static async Task<string> Sha256FileAsync(string path, CancellationToken token)
        {
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                byte[] hash = await SHA256.HashDataAsync(stream, token).ConfigureAwait(false);
                return Convert.ToHexString(hash).ToLowerInvariant();
            }
        }

        private static async Task<ArchivedManifest> ReadManifestAsync(ZipArchiveEntry entry, CancellationToken token)
        {
            if (entry.Length > 1024 * 1024) throw new DatabaseBackupException("backup_manifest_invalid", true);
            try
            {
                using (Stream stream = entry.Open())
                {
                    ArchivedManifest? manifest = await JsonSerializer.DeserializeAsync<ArchivedManifest>(stream, ManifestOptions, token).ConfigureAwait(false);
                    return manifest ?? throw new DatabaseBackupException("backup_manifest_invalid", true);
                }
            }
            catch (JsonException ex)
            {
                throw new DatabaseBackupException("backup_manifest_invalid", true, ex);
            }
        }

        private static async Task<bool> IsValidArmadaSqliteAsync(string path, CancellationToken token)
        {
            try
            {
                using (SqliteConnection connection = new SqliteConnection("Data Source=" + path + ";Mode=ReadOnly;Pooling=False"))
                {
                    await connection.OpenAsync(token).ConfigureAwait(false);
                    using (SqliteCommand integrity = connection.CreateCommand())
                    {
                        integrity.CommandText = "PRAGMA integrity_check;";
                        object? check = await integrity.ExecuteScalarAsync(token).ConfigureAwait(false);
                        if (!String.Equals(Convert.ToString(check), "ok", StringComparison.OrdinalIgnoreCase)) return false;
                    }
                    using (SqliteCommand schema = connection.CreateCommand())
                    {
                        schema.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='schema_migrations';";
                        object? table = await schema.ExecuteScalarAsync(token).ConfigureAwait(false);
                        return table != null && table != DBNull.Value;
                    }
                }
            }
            catch (SqliteException)
            {
                return false;
            }
        }

        private static void DeleteDirectoryQuietly(string path)
        {
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, true);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // A leftover private working directory has a unique name and holds no success claim; the
                // operation result already reflects whether the backup or restore completed.
            }
        }

        private static JsonSerializerOptions CreateManifestOptions()
        {
            JsonSerializerOptions options = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                PropertyNameCaseInsensitive = true,
                WriteIndented = true
            };
            options.Converters.Add(new JsonStringEnumConverter());
            return options;
        }

        private sealed class ArchivedManifest
        {
            public DatabaseTypeEnum? DatabaseType { get; set; }
            public int SchemaVersion { get; set; }
        }
    }
}
