namespace Armada.Test.Shared.Infrastructure
{
    using System;
    using System.IO;
    using System.Threading.Tasks;
    using Armada.Core.Database.Sqlite;
    using SyslogLogging;

    /// <summary>
    /// Creates disposable, fully-initialized SQLite databases for tests. Uses temp files
    /// rather than in-memory databases because in-memory stores close when the last
    /// connection drops, which conflicts with the driver's per-operation connection pattern.
    /// Every call yields a fresh, isolated database.
    /// </summary>
    public static class TestDatabaseHelper
    {
        #region Public-Methods

        /// <summary>
        /// Create and initialize a temp-file SQLite database driver for testing. The returned
        /// wrapper disposes the driver and deletes the temp file when disposed.
        /// </summary>
        /// <returns>An initialized, disposable <see cref="TestDatabase"/>.</returns>
        public static async Task<TestDatabase> CreateDatabaseAsync()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;

            string tempFile = Path.Combine(Path.GetTempPath(), "armada_test_" + Guid.NewGuid().ToString("N") + ".db");
            string connectionString = "Data Source=" + tempFile;

            SqliteDatabaseDriver driver = new SqliteDatabaseDriver(connectionString, logging);
            await driver.InitializeAsync().ConfigureAwait(false);
            return new TestDatabase(driver, tempFile, connectionString);
        }

        #endregion
    }
}
