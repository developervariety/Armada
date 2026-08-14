namespace Test.Shared.Infrastructure
{
    using System;
    using Armada.Core.Database;

    /// <summary>
    /// Wraps an initialized database driver for a single test case and tears the backing store down on
    /// dispose. For SQLite the store is a temp file that is deleted; for a server backend it is a uniquely
    /// named database that is dropped. Each case that needs a database creates one via
    /// <see cref="TestDatabaseHelper.CreateDatabaseAsync"/>, giving every case a fully isolated store with no
    /// cross-suite or cross-case state bleed, regardless of the configured provider.
    /// </summary>
    public sealed class TestDatabase : IDisposable
    {
        #region Public-Members

        /// <summary>
        /// The initialized database driver under test. Typed as the provider-agnostic base so tests run
        /// unchanged against SQLite, PostgreSQL, MySQL, or SQL Server.
        /// </summary>
        public DatabaseDriver Driver { get; }

        /// <summary>
        /// The connection string used for this test database.
        /// </summary>
        public string ConnectionString { get; }

        #endregion

        #region Private-Members

        private readonly Action _Cleanup;

        #endregion

        #region Constructors-and-Factories

        internal TestDatabase(DatabaseDriver driver, string connectionString, Action cleanup)
        {
            Driver = driver ?? throw new ArgumentNullException(nameof(driver));
            ConnectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
            _Cleanup = cleanup ?? throw new ArgumentNullException(nameof(cleanup));
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Dispose the driver and tear down the backing store (delete the temp file or drop the database).
        /// Cleanup failures are swallowed so a teardown problem never masks the test result.
        /// </summary>
        public void Dispose()
        {
            Driver.Dispose();
            try
            {
                _Cleanup();
            }
            catch
            {
                // Best effort: a failed drop/delete must not fail the test.
            }
        }

        #endregion
    }
}
