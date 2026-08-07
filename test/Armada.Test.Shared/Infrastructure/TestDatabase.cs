namespace Armada.Test.Shared.Infrastructure
{
    using System;
    using System.IO;
    using Armada.Core.Database.Sqlite;

    /// <summary>
    /// Wraps an initialized SQLite driver backed by a temp file and deletes that file on
    /// dispose. Each test case that needs a database creates one of these via
    /// <see cref="TestDatabaseHelper.CreateDatabaseAsync"/>, giving every case a fully
    /// isolated store with no cross-suite or cross-case state bleed.
    /// </summary>
    public sealed class TestDatabase : IDisposable
    {
        #region Public-Members

        /// <summary>
        /// The initialized SQLite database driver under test.
        /// </summary>
        public SqliteDatabaseDriver Driver { get; }

        /// <summary>
        /// The connection string used for this test database.
        /// </summary>
        public string ConnectionString { get; }

        #endregion

        #region Private-Members

        private readonly string _TempFile;

        #endregion

        #region Constructors-and-Factories

        internal TestDatabase(SqliteDatabaseDriver driver, string tempFile, string connectionString)
        {
            Driver = driver ?? throw new ArgumentNullException(nameof(driver));
            _TempFile = tempFile ?? throw new ArgumentNullException(nameof(tempFile));
            ConnectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Dispose the driver and delete the backing temp file (best effort).
        /// </summary>
        public void Dispose()
        {
            Driver.Dispose();
            try
            {
                if (File.Exists(_TempFile)) File.Delete(_TempFile);
            }
            catch
            {
                // Temp-file cleanup is best effort; a lingering handle on some platforms
                // must not fail an otherwise-passing test.
            }
        }

        #endregion
    }
}
