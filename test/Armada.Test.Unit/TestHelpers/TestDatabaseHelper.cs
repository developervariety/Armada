namespace Armada.Test.Unit.TestHelpers
{
    using Microsoft.Data.Sqlite;
    using Armada.Core.Database.Sqlite;
    using SyslogLogging;

    /// <summary>
    /// Helper for creating disposable SQLite databases for testing.
    /// Uses temp files since in-memory databases close when the last connection drops,
    /// which conflicts with the driver's per-operation connection pattern.
    /// </summary>
    public static class TestDatabaseHelper
    {
        #region Private-Members

        private static readonly SemaphoreSlim _TemplateLock = new SemaphoreSlim(1, 1);
        private static string? _TemplatePath;

        #endregion

        #region Public-Methods

        /// <summary>
        /// Create an initialized temp-file SQLite database driver for testing.
        /// The returned wrapper disposes the driver and deletes the temp file.
        /// Access the driver via the Driver property.
        /// </summary>
        /// <remarks>
        /// The schema is built ONCE per test process into a template file, and every test
        /// database is a file copy of that template. Running the driver's migrations per test
        /// meant 34 CREATE TABLE plus 233 CREATE INDEX statements, a transaction per migration,
        /// and the default tenant/user/credential seed for each of the ~1600 databases a full
        /// unit run creates -- the dominant cost of the run. A copy carries the identical
        /// schema, migration rows, and seed data, so callers see no behavioral difference.
        /// </remarks>
        public static async Task<TestDatabase> CreateDatabaseAsync()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;

            string templatePath = await EnsureTemplateAsync().ConfigureAwait(false);
            string tempFile = Path.Combine(Path.GetTempPath(), "armada_test_" + Guid.NewGuid().ToString("N") + ".db");
            File.Copy(templatePath, tempFile);

            // Pooling keeps SQLite handles (and their WAL/SHM sidecars) alive after
            // each disposable test database is deleted. A full unit run creates
            // thousands of databases, so disable pooling for these isolated files.
            string connectionString = $"Data Source={tempFile};Pooling=False";

            SqliteDatabaseDriver driver = new SqliteDatabaseDriver(connectionString, logging);
            return new TestDatabase(driver, tempFile, connectionString);
        }

        #endregion

        #region Private-Methods

        /// <summary>
        /// Build the schema template on first use and return its path.
        /// </summary>
        private static async Task<string> EnsureTemplateAsync()
        {
            if (_TemplatePath != null) return _TemplatePath;

            await _TemplateLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_TemplatePath != null) return _TemplatePath;

                LoggingModule logging = new LoggingModule();
                logging.Settings.EnableConsole = false;

                string path = Path.Combine(
                    Path.GetTempPath(),
                    "armada_test_template_" + Guid.NewGuid().ToString("N") + ".db");
                string connectionString = $"Data Source={path};Pooling=False";

                using (SqliteDatabaseDriver driver = new SqliteDatabaseDriver(connectionString, logging))
                {
                    await driver.InitializeAsync().ConfigureAwait(false);
                }

                // Fold the write-ahead log back into the database file. Without this the schema
                // lives partly in the -wal sidecar and a copy of the .db alone is incomplete.
                await CheckpointAsync(connectionString).ConfigureAwait(false);
                SqliteConnection.ClearAllPools();
                TryDelete(path + "-wal");
                TryDelete(path + "-shm");

                _TemplatePath = path;
                return _TemplatePath;
            }
            finally
            {
                _TemplateLock.Release();
            }
        }

        private static async Task CheckpointAsync(string connectionString)
        {
            using (SqliteConnection conn = new SqliteConnection(connectionString))
            {
                await conn.OpenAsync().ConfigureAwait(false);
                using (SqliteCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                    await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                }
            }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { }
        }

        #endregion
    }
}
