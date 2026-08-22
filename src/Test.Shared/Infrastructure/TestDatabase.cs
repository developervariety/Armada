namespace Test.Shared.Infrastructure
{
    using System;
    using Armada.Core.Database;

    /// <summary>
    /// Wraps an initialized database driver for a single test case. For SQLite the store is a per-case temp
    /// file that is deleted on dispose. For a server backend the harness migrates one shared database per run
    /// and hands each case a reset-to-clean view of it (owns the driver = false), so dispose is a no-op on the
    /// shared driver -- the expensive migrate-and-seed happens once instead of per case. Each case still sees
    /// a clean, isolated store with no cross-case state bleed, regardless of the configured provider.
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

        private readonly Action? _Cleanup;
        private readonly bool _OwnsDriver;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Wrap a driver for a single case. When <paramref name="ownsDriver"/> is true the driver is disposed
        /// and <paramref name="cleanup"/> runs on dispose (SQLite temp-file path). When false the driver is a
        /// shared, reused instance and dispose leaves it untouched (server shared-database path).
        /// </summary>
        internal TestDatabase(DatabaseDriver driver, string connectionString, Action? cleanup, bool ownsDriver = true)
        {
            Driver = driver ?? throw new ArgumentNullException(nameof(driver));
            ConnectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
            _Cleanup = cleanup;
            _OwnsDriver = ownsDriver;
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// For an owned (SQLite) database, dispose the driver and tear down the backing store. For the shared
        /// server database, leave the reused driver in place for the next case. Cleanup failures are swallowed
        /// so a teardown problem never masks the test result.
        /// </summary>
        public void Dispose()
        {
            if (!_OwnsDriver) return;

            Driver.Dispose();
            try
            {
                _Cleanup?.Invoke();
            }
            catch
            {
                // Best effort: a failed drop/delete must not fail the test.
            }
        }

        #endregion
    }
}
