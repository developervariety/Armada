namespace Test.Shared.Infrastructure
{
    using System;
    using System.IO;
    using System.Threading.Tasks;
    using Microsoft.Data.Sqlite;
    using Armada.Core.Database.Sqlite;
    using SyslogLogging;

    /// <summary>
    /// Creates disposable, fully-initialized SQLite databases for tests. Every call yields a fresh,
    /// isolated temp-file database.
    ///
    /// Rather than run all schema migrations (and default-data seeding) from scratch on every call --
    /// which costs ~250-300ms per test in fsync'd migration transactions and dominates suite runtime --
    /// the migrated-and-seeded schema is built exactly once into a template file, and each database is a
    /// fast file copy of that template (~5-10ms). The copy is a byte-for-byte clone, so tests get the
    /// same starting state as a full InitializeAsync would produce, without the per-test migration tax.
    /// </summary>
    public static class TestDatabaseHelper
    {
        #region Private-Members

        private static readonly object _TemplateLock = new object();
        private static string? _TemplatePath;

        #endregion

        #region Public-Methods

        /// <summary>
        /// Create and initialize a temp-file SQLite database driver for testing. The returned
        /// wrapper disposes the driver and deletes the temp file when disposed.
        /// </summary>
        /// <returns>An initialized, disposable <see cref="TestDatabase"/>.</returns>
        public static Task<TestDatabase> CreateDatabaseAsync()
        {
            string template = EnsureTemplate();

            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;

            string tempFile = TestTemp.NewFile("test", ".db");
            File.Copy(template, tempFile);

            // Disable connection pooling for the per-test file. Pooled connections keep an OS handle on
            // the SQLite file open after the driver is disposed, which makes the File.Delete in
            // TestDatabase.Dispose fail silently and leaves the temp .db behind. With pooling off, closing
            // the driver's connections releases the file immediately so each test database is deleted the
            // moment its TestDatabase is disposed.
            string connectionString = "Data Source=" + tempFile + ";Pooling=False";
            // Schema and seed data are already present from the template copy, so InitializeAsync
            // (all migrations + seeding) is intentionally not run here.
            SqliteDatabaseDriver driver = new SqliteDatabaseDriver(connectionString, logging);
            return Task.FromResult(new TestDatabase(driver, tempFile, connectionString));
        }

        /// <summary>
        /// Copy the migrated-and-seeded schema template over the supplied database file path. A server or
        /// driver that subsequently runs <c>InitializeAsync</c> against this file finds the schema already
        /// at the current version -- so it skips the full migration run and re-seeding (both are guarded)
        /// and boots almost instantly. Used by the end-to-end fixture to avoid paying the migration cost
        /// on every server start. The parent directory is created if it does not already exist.
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

                // Release the pooled connection so the template file is fully closed and unlocked for
                // copying, and any rollback journal is finalized.
                SqliteConnection.ClearAllPools();

                _TemplatePath = path;
                return path;
            }
        }

        #endregion
    }
}
