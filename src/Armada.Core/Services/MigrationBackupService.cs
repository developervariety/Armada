namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Settings;
    using SyslogLogging;

    /// <summary>
    /// Requires a native backup with isolated restore proof before startup upgrades a versioned database.
    /// </summary>
    public sealed class MigrationBackupService
    {
        #region Private-Members

        private readonly ArmadaSettings _Settings;
        private readonly LoggingModule _Logging;
        private readonly string _Header = "[MigrationBackupService] ";

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate the startup backup gate.
        /// </summary>
        /// <param name="settings">Application settings.</param>
        /// <param name="logging">Logging module.</param>
        public MigrationBackupService(ArmadaSettings settings, LoggingModule logging)
        {
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Inspect the stored version and retain a verified backup when an upgrade is pending.
        /// Call before <see cref="DatabaseDriver.InitializeAsync"/>; a failure must abort startup.
        /// Fresh and current databases do not need a migration backup.
        /// </summary>
        /// <param name="driver">The configured, not yet initialized database driver.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The verified archive, or null when no versioned upgrade is pending.</returns>
        /// <exception cref="DatabaseBackupException">Backup or isolated restore proof failed.</exception>
        public async Task<DatabaseBackupResult?> PrepareAsync(DatabaseDriver driver, CancellationToken token = default)
        {
            if (driver == null) throw new ArgumentNullException(nameof(driver));
            token.ThrowIfCancellationRequested();
            if (_Settings.Database.Type == DatabaseTypeEnum.Sqlite && !File.Exists(_Settings.Database.Filename))
                return null;

            int currentVersion = await driver.GetSchemaVersionAsync(token).ConfigureAwait(false);
            int targetVersion = LatestSchemaVersion(_Settings.Database.Type);
            if (currentVersion <= 0 || currentVersion >= targetVersion) return null;

            string archive = Path.Combine(_Settings.DataDirectory, "backups", "pre-migration-"
                + DateTime.UtcNow.ToString("yyyy-MM-dd-HHmmss") + "-v" + currentVersion + "-to-v" + targetVersion
                + "-" + Guid.NewGuid().ToString("N") + ".zip");
            _Logging.Info(_Header + "schema upgrade v" + currentVersion + " to v" + targetVersion
                + " requires a verified native backup before migration");
            try
            {
                DatabaseBackupResult backup = await new DatabaseBackupService(driver, _Settings)
                    .BackupAsync(archive, token).ConfigureAwait(false);
                _Logging.Info(_Header + "native backup and isolated restore verified; retained archive: " + backup.Path);
                return backup;
            }
            catch (DatabaseBackupException ex)
            {
                _Logging.Warn(_Header + "startup stopped before migration: " + ex.FailureReason);
                throw;
            }
        }

        #endregion

        #region Private-Methods

        private static int LatestSchemaVersion(DatabaseTypeEnum provider)
        {
            IReadOnlyList<SchemaMigration> migrations = provider switch
            {
                DatabaseTypeEnum.Sqlite => Database.Sqlite.Queries.TableQueries.GetMigrations(),
                DatabaseTypeEnum.Postgresql => Database.Postgresql.Queries.TableQueries.GetMigrations(),
                DatabaseTypeEnum.Mysql => Database.Mysql.MysqlDatabaseDriver.GetMigrations(),
                DatabaseTypeEnum.SqlServer => Database.SqlServer.Queries.TableQueries.GetMigrations(),
                _ => throw new ArgumentOutOfRangeException(nameof(provider))
            };
            return migrations.Max(migration => migration.Version);
        }

        #endregion
    }
}
