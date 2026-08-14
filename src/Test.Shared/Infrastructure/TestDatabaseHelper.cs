namespace Test.Shared.Infrastructure
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Data.Sqlite;
    using Armada.Core.Database;
    using Armada.Core.Database.Sqlite;
    using Armada.Core.Settings;
    using SyslogLogging;

    /// <summary>
    /// Creates fully-initialized databases for tests. Every call yields a clean, isolated store.
    ///
    /// The provider is selected by <see cref="TestDatabaseConfig"/>. To keep the identical suite fast on every
    /// backend, the expensive migrate-and-seed is paid once and reused: for SQLite the migrated-and-seeded
    /// schema is built once into a template file and each database is a fast file copy (~5-10ms); for a server
    /// provider (PostgreSQL, MySQL, SQL Server) one shared database is migrated and seeded once per run, and
    /// each call resets it to a clean state (every table truncated, default data re-seeded) instead of
    /// re-migrating -- turning a ~30s-per-case cost on MySQL into a sub-second reset. The same suite still
    /// validates every provider's schema and DB-method implementations.
    /// </summary>
    public static class TestDatabaseHelper
    {
        #region Private-Members

        private static readonly object _TemplateLock = new object();
        private static string? _TemplatePath;

        private static readonly SemaphoreSlim _ServerInitLock = new SemaphoreSlim(1, 1);
        private static DatabaseDriver? _SharedServerDriver;
        private static string? _SharedServerConnectionString;
        private static List<string>? _SharedServerTables;

        #endregion

        #region Public-Methods

        /// <summary>
        /// Create and initialize a database for the configured provider and hand it to a single test case.
        /// For SQLite each call is an isolated temp-file copy of the migrated template. For a server provider
        /// the harness migrates one shared database per run and returns a reset-to-clean view of it: every
        /// table is emptied and default data re-seeded, so the case sees the same clean state a fresh database
        /// would have -- without paying the (30s on MySQL) migrate-and-seed tax per case. The returned wrapper
        /// is disposed by the case; for the shared server database dispose is a no-op on the reused driver.
        /// </summary>
        /// <returns>An initialized, disposable <see cref="TestDatabase"/>.</returns>
        public static async Task<TestDatabase> CreateDatabaseAsync()
        {
            if (TestDatabaseConfig.IsSqlite) return CreateSqliteDatabase();
            return await CreateServerDatabaseAsync().ConfigureAwait(false);
        }

        /// <summary>
        /// Copy the migrated-and-seeded SQLite schema template over the supplied database file path. A server
        /// or driver that subsequently runs <c>InitializeAsync</c> against this file finds the schema already
        /// at the current version -- so it skips the full migration run and re-seeding (both are guarded) and
        /// boots almost instantly. Used by the end-to-end fixture, which is SQLite-backed, to avoid paying the
        /// migration cost on every server start. The parent directory is created if it does not already exist.
        /// </summary>
        /// <param name="destinationPath">Absolute path of the SQLite database file to (over)write.</param>
        public static void SeedDatabaseFile(string destinationPath)
        {
            if (String.IsNullOrEmpty(destinationPath)) throw new ArgumentNullException(nameof(destinationPath));

            string template = EnsureTemplate();
            string? directory = Path.GetDirectoryName(destinationPath);
            if (!String.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.Copy(template, destinationPath, true);
        }

        #endregion

        #region Private-Methods

        private static TestDatabase CreateSqliteDatabase()
        {
            string template = EnsureTemplate();

            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;

            string tempFile = TestTemp.NewFile("test", ".db");
            File.Copy(template, tempFile);

            // Disable connection pooling for the per-test file. Pooled connections keep an OS handle on the
            // SQLite file open after the driver is disposed, which makes the temp-file delete fail silently
            // and leaves the .db behind. With pooling off, closing the driver's connections releases the file
            // immediately so each test database is deleted the moment its TestDatabase is disposed.
            string connectionString = "Data Source=" + tempFile + ";Pooling=False";
            // Schema and seed data are already present from the template copy, so InitializeAsync (all
            // migrations + seeding) is intentionally not run here.
            SqliteDatabaseDriver driver = new SqliteDatabaseDriver(connectionString, logging);
            return new TestDatabase(driver, connectionString, () => TestTemp.TryDelete(tempFile));
        }

        /// <summary>
        /// Return a clean view of the shared server database. The shared database is migrated and seeded once
        /// per run (<see cref="EnsureSharedServerDatabaseAsync"/>); each call empties every table and re-seeds
        /// default data so the case starts from the same clean state a freshly migrated database would have.
        /// The returned wrapper does not own the driver -- disposing it leaves the shared database in place for
        /// the next case. Cases run sequentially and never hold two databases at once, so a single shared
        /// database is safe.
        /// </summary>
        private static async Task<TestDatabase> CreateServerDatabaseAsync()
        {
            await EnsureSharedServerDatabaseAsync().ConfigureAwait(false);

            // Empty every table (schema_migrations is preserved) then re-seed defaults by re-running
            // InitializeAsync, which finds the schema already current, skips all migrations, and re-seeds the
            // default tenant/user/credential because the tables are now empty.
            await TestDatabaseProvisioner.ResetDatabaseAsync(_SharedServerConnectionString!, _SharedServerTables!).ConfigureAwait(false);
            await _SharedServerDriver!.InitializeAsync().ConfigureAwait(false);

            return new TestDatabase(_SharedServerDriver!, _SharedServerConnectionString!, cleanup: null, ownsDriver: false);
        }

        /// <summary>
        /// Lazily create, migrate, and seed the single shared server database for this run, and capture the
        /// list of tables to reset between cases. Any pre-existing database of the same name (from an earlier
        /// run against a persistent server) is dropped first so the run starts from a known-clean schema.
        /// </summary>
        private static async Task EnsureSharedServerDatabaseAsync()
        {
            if (_SharedServerDriver != null) return;

            await _ServerInitLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_SharedServerDriver != null) return;

                string databaseName = TestDatabaseConfig.BaseDatabaseName + "_shared";
                await TestDatabaseProvisioner.DropDatabaseAsync(databaseName).ConfigureAwait(false);
                await TestDatabaseProvisioner.CreateDatabaseAsync(databaseName).ConfigureAwait(false);

                DatabaseSettings settings = TestDatabaseConfig.BuildSettings(databaseName);
                DatabaseDriver driver = await DatabaseDriverFactory.CreateAndInitializeAsync(settings).ConfigureAwait(false);

                _SharedServerConnectionString = settings.GetConnectionString();
                _SharedServerTables = await TestDatabaseProvisioner.GetTableNamesAsync(_SharedServerConnectionString).ConfigureAwait(false);
                _SharedServerDriver = driver;
            }
            finally
            {
                _ServerInitLock.Release();
            }
        }

        private static string EnsureTemplate()
        {
            string? existing = _TemplatePath;
            if (existing != null && File.Exists(existing)) return existing;

            lock (_TemplateLock)
            {
                if (_TemplatePath != null && File.Exists(_TemplatePath)) return _TemplatePath;

                LoggingModule logging = new LoggingModule();
                logging.Settings.EnableConsole = false;

                string path = TestTemp.NewFile("test_template", ".db");
                using (SqliteDatabaseDriver driver = new SqliteDatabaseDriver("Data Source=" + path, logging))
                {
                    driver.InitializeAsync().GetAwaiter().GetResult();
                }

                // Release the pooled connection so the template file is fully closed and unlocked for copying,
                // and any rollback journal is finalized.
                SqliteConnection.ClearAllPools();

                _TemplatePath = path;
                return path;
            }
        }

        #endregion
    }
}
