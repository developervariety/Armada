namespace Armada.Test.Shared.Infrastructure
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

            string tempFile = Path.Combine(Path.GetTempPath(), "armada_test_" + Guid.NewGuid().ToString("N") + ".db");
            File.Copy(template, tempFile);

            string connectionString = "Data Source=" + tempFile;
            // Schema and seed data are already present from the template copy, so InitializeAsync
            // (all migrations + seeding) is intentionally not run here.
            SqliteDatabaseDriver driver = new SqliteDatabaseDriver(connectionString, logging);
            return Task.FromResult(new TestDatabase(driver, tempFile, connectionString));
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

                string path = Path.Combine(Path.GetTempPath(), "armada_test_template_" + Guid.NewGuid().ToString("N") + ".db");
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
