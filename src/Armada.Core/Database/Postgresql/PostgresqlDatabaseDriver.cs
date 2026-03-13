namespace Armada.Core.Database.Postgresql
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Npgsql;
    using SyslogLogging;
    using Armada.Core.Database.Postgresql.Implementations;
    using Armada.Core.Settings;

    /// <summary>
    /// PostgreSQL implementation of the Armada database driver.
    /// </summary>
    public class PostgresqlDatabaseDriver : DatabaseDriver
    {
        #region Public-Members

        /// <summary>
        /// Connection string for the PostgreSQL database.
        /// </summary>
        internal string ConnectionString
        {
            get { return _ConnectionString; }
        }

        #endregion

        #region Private-Members

        private string _Header = "[PostgresqlDatabaseDriver] ";
        private DatabaseSettings _Settings;
        private string _ConnectionString;
        private LoggingModule _Logging;
        private NpgsqlDataSource _DataSource;
        private bool _Disposed = false;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate the PostgreSQL database driver.
        /// </summary>
        /// <param name="settings">Database settings.</param>
        /// <param name="logging">Logging module.</param>
        public PostgresqlDatabaseDriver(DatabaseSettings settings, LoggingModule logging)
        {
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
            _ConnectionString = settings.GetConnectionString();
            _DataSource = NpgsqlDataSource.Create(_ConnectionString);

            InitializeImplementations();
        }

        /// <summary>
        /// Instantiate the PostgreSQL database driver with a raw connection string.
        /// </summary>
        /// <param name="connectionString">PostgreSQL connection string.</param>
        /// <param name="logging">Logging module.</param>
        public PostgresqlDatabaseDriver(string connectionString, LoggingModule logging)
        {
            _ConnectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
            _Settings = new DatabaseSettings();
            _DataSource = NpgsqlDataSource.Create(_ConnectionString);

            InitializeImplementations();
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Initialize the database, creating tables if they do not exist.
        /// </summary>
        /// <param name="token">Cancellation token.</param>
        public override async Task InitializeAsync(CancellationToken token = default)
        {
            _Logging.Info(_Header + "initializing database");

            // PostgreSQL initialization will be implemented when the driver is fully wired up.
            // For now, this is a placeholder to resolve the compilation dependency.
            await Task.CompletedTask;
        }

        /// <summary>
        /// Dispose of the database driver.
        /// </summary>
        public override void Dispose()
        {
            if (_Disposed) return;
            _Disposed = true;
            _DataSource?.Dispose();
        }

        /// <summary>
        /// Create a new NpgsqlConnection using the connection string.
        /// </summary>
        /// <returns>An unopened NpgsqlConnection.</returns>
        internal NpgsqlConnection CreateConnection()
        {
            return new NpgsqlConnection(_ConnectionString);
        }

        #endregion

        #region Private-Methods

        private void InitializeImplementations()
        {
            Fleets = new FleetMethods(this, _Settings, _Logging);
            Vessels = new VesselMethods(this, _Settings, _Logging);
            Captains = new CaptainMethods(this, _Settings, _Logging);
            Missions = new MissionMethods(this, _Settings, _Logging);
            Voyages = new VoyageMethods(this, _Settings, _Logging);
            Docks = new DockMethods(this, _Settings, _Logging);
            Signals = new SignalMethods(_DataSource);
            Events = new EventMethods(_DataSource);
            MergeEntries = new MergeEntryMethods(_DataSource);
        }

        #endregion
    }
}
