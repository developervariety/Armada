namespace Armada.Core.Database.Sqlite
{
    using System;
    using System.Collections.Generic;
    using System.Data;
    using System.Globalization;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Data.Sqlite;
    using SyslogLogging;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Database.Sqlite.Implementations;
    using Armada.Core.Database.Sqlite.Queries;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Settings;

    /// <summary>
    /// SQLite implementation of the Armada database driver.
    /// </summary>
    public class SqliteDatabaseDriver : DatabaseDriver
    {
        #region Public-Members

        /// <summary>
        /// Connection string for the SQLite database.
        /// </summary>
        internal string ConnectionString
        {
            get { return _ConnectionString; }
        }

        #endregion

        #region Internal-Members

        /// <summary>
        /// Creates the migration ledger.
        /// </summary>
        internal const string SchemaMigrationsTable = @"CREATE TABLE IF NOT EXISTS schema_migrations (
                        version INTEGER PRIMARY KEY,
                        description TEXT NOT NULL,
                        applied_utc TEXT NOT NULL
                    );";

        /// <summary>
        /// How this provider's stored values convert to model values. SQLite stores booleans as
        /// integers where 1 is true, and timestamps as ISO 8601 text.
        /// </summary>
        internal static readonly StoredValueConverter StoredValues = new StoredValueConverter("SQLite", integerBooleans: true);

        /// <summary>
        /// SQLite stored forms: every timestamp as ISO 8601 text, every boolean as an integer.
        /// </summary>
        internal static readonly StoredValueBinder StoredBinder = new StoredValueBinder(
            "SQLite",
            StoredTimestampEnum.Iso8601Text,
            new Dictionary<string, StoredTimestampEnum>(),
            integerBooleans: true,
            integerBooleanColumns: Array.Empty<string>(),
            timestampDbType: DbType.String,
            zonedTimestampDbType: DbType.String);

        #endregion

        #region Private-Members

        private string _Header = "[SqliteDatabaseDriver] ";
        private DatabaseSettings _Settings;
        private string _ConnectionString;
        private StoredDialect _Stored = null!;
        private LoggingModule _Logging;
        private bool _Disposed = false;

        private static readonly string _Iso8601Format = "yyyy-MM-ddTHH:mm:ss.fffffffZ";

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate the SQLite database driver.
        /// </summary>
        /// <param name="settings">Database settings.</param>
        /// <param name="logging">Logging module.</param>
        public SqliteDatabaseDriver(DatabaseSettings settings, LoggingModule logging)
        {
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
            _ConnectionString = settings.GetConnectionString();

            InitializeImplementations();
        }

        /// <summary>
        /// Instantiate the SQLite database driver with a raw connection string.
        /// </summary>
        /// <param name="connectionString">SQLite connection string.</param>
        /// <param name="logging">Logging module.</param>
        public SqliteDatabaseDriver(string connectionString, LoggingModule logging)
        {
            _ConnectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
            _Settings = new DatabaseSettings();

            InitializeImplementations();
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Initialize the database, running any pending schema migrations.
        /// </summary>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Task.</returns>
        public override async Task InitializeAsync(CancellationToken token = default)
        {
            _Logging.Info(_Header + "initializing database");

            using (SqliteConnection conn = new SqliteConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);

                using (SqliteCommand walCmd = conn.CreateCommand())
                {
                    walCmd.CommandText = "PRAGMA journal_mode=WAL;";
                    await walCmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }

                using (SqliteCommand fkCmd = conn.CreateCommand())
                {
                    fkCmd.CommandText = "PRAGMA foreign_keys=ON;";
                    await fkCmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }

                // Create migration tracking table
                using (SqliteCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = SchemaMigrationsTable;
                    await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }

                // Apply pending migrations. The ledger read refuses a skipped lower version.
                List<SchemaMigration> migrations = TableQueries.GetMigrations();
                int currentVersion = await AppliedMigrationLedger.ReadCurrentVersionAsync(conn, migrations, Armada.Core.Enums.DatabaseTypeEnum.Sqlite, token).ConfigureAwait(false);
                int applied = 0;

                foreach (SchemaMigration migration in migrations)
                {
                    if (migration.Version == 52)
                        await DefaultDatabaseIdentity.EnsureAsync(conn, Armada.Core.Enums.DatabaseTypeEnum.Sqlite, MigrationCheckpoint, token).ConfigureAwait(false);
                    if (migration.Version > currentVersion) MigrationCheckpoint?.Invoke(migration.Version, -1);
                    if (migration.Version <= currentVersion) continue;

                    _Logging.Info(_Header + "applying migration v" + migration.Version + ": " + migration.Description);

                    using (SqliteTransaction tx = conn.BeginTransaction(deferred: false))
                    {
                        // Another initializer can commit while this connection waits for the
                        // SQLite write lock. Read the ledger again inside that lock.
                        using (SqliteCommand versionCommand = conn.CreateCommand())
                        {
                            versionCommand.Transaction = tx;
                            versionCommand.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_migrations;";
                            int lockedVersion = Convert.ToInt32(await versionCommand.ExecuteScalarAsync(token).ConfigureAwait(false));
                            if (migration.Version <= lockedVersion) continue;
                        }
                        if (migration.Version == 88)
                            await ModelEndpointSchemaGuard.EnsureAsync(conn, tx, Armada.Core.Enums.DatabaseTypeEnum.Sqlite, token).ConfigureAwait(false);
                        if (migration.Version == 89)
                            await CaptainModelEndpointSchemaGuard.EnsureAsync(conn, tx, Armada.Core.Enums.DatabaseTypeEnum.Sqlite, true, token).ConfigureAwait(false);
                        if (migration.Version == 90)
                            await HarborRunnerSchemaGuard.EnsureAsync(conn, tx, Armada.Core.Enums.DatabaseTypeEnum.Sqlite, token).ConfigureAwait(false);
                        if (migration.Version == 104)
                            await MemoryProposalSchema.EnsureAsync(conn, tx, Armada.Core.Enums.DatabaseTypeEnum.Sqlite, token).ConfigureAwait(false);
                        for (int statementOrdinal = 0; statementOrdinal < migration.Statements.Count; statementOrdinal++)
                        {
                            string sql = migration.Statements[statementOrdinal];
                            using (SqliteCommand cmd = conn.CreateCommand())
                            {
                                cmd.Transaction = tx;
                                cmd.CommandText = sql;
                                try
                                {
                                    if (migration.Version == 83 || migration.Version == 84 || migration.Version == 85 || migration.Version == 87 || (migration.Version == 89 && statementOrdinal == 0))
                                        await AdditiveColumnMigration.ExecuteAsync(conn, tx, Armada.Core.Enums.DatabaseTypeEnum.Sqlite, sql, token).ConfigureAwait(false);
                                    else
                                        await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                                    MigrationCheckpoint?.Invoke(migration.Version, statementOrdinal);
                                }
                                catch (SqliteException ex) when (ex.SqliteErrorCode == 1 && ex.Message.Contains("duplicate column name"))
                                {
                                    // Column already exists in the CREATE TABLE definition.
                                    // This happens when migrations add columns that were later
                                    // incorporated into the initial schema. Safe to skip.
                                    _Logging.Info(_Header + "migration v" + migration.Version + ": column already exists, skipping");
                                }
                            }
                        }

                        // Record migration
                        using (SqliteCommand cmd = conn.CreateCommand())
                        {
                            cmd.Transaction = tx;
                            cmd.CommandText = "INSERT INTO schema_migrations (version, description, applied_utc) VALUES (@v, @d, @t);";
                            cmd.Parameters.AddWithValue("@v", migration.Version);
                            cmd.Parameters.AddWithValue("@d", migration.Description);
                            cmd.Parameters.AddWithValue("@t", ToIso8601(DateTime.UtcNow));
                            await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                        }

                        tx.Commit();
                        applied++;
                        MigrationCheckpoint?.Invoke(migration.Version, -2);
                    }
                }

                if (applied > 0)
                    _Logging.Info(_Header + "applied " + applied + " migration(s), schema now at v" + migrations[migrations.Count - 1].Version);
                else
                    _Logging.Info(_Header + "schema is up to date at v" + currentVersion);

                // The stored column forms the binder writes must match the migrated schema before any write.
                await StoredBinderSchemaGuard.EnsureAsync(conn, Armada.Core.Enums.DatabaseTypeEnum.Sqlite, token).ConfigureAwait(false);
            }

            _Logging.Info(_Header + "database initialized successfully");

        }

        /// <summary>
        /// Get the current schema version.
        /// </summary>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Current schema version number, or 0 if no migrations have been applied.</returns>
        public override async Task<int> GetSchemaVersionAsync(CancellationToken token = default)
        {
            using (SqliteConnection conn = new SqliteConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);

                // Check if schema_migrations table exists
                using (SqliteCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='schema_migrations';";
                    object? result = await cmd.ExecuteScalarAsync(token).ConfigureAwait(false);
                    if (result == null || result == DBNull.Value) return 0;
                }

                using (SqliteCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_migrations;";
                    object? result = await cmd.ExecuteScalarAsync(token).ConfigureAwait(false);
                    if (result != null && result != DBNull.Value) return Convert.ToInt32(result);
                    return 0;
                }
            }
        }

        /// <summary>
        /// Dispose of resources.
        /// </summary>
        public override void Dispose()
        {
            if (_Disposed) return;
            _Disposed = true;
            _Logging.Info(_Header + "disposed");
        }

        #endregion

        #region Internal-Methods

        /// <summary>
        /// Convert a DateTime to ISO 8601 format string.
        /// </summary>
        /// <param name="dt">DateTime value.</param>
        /// <returns>ISO 8601 formatted string.</returns>
        internal static string ToIso8601(DateTime dt)
        {
            return dt.ToUniversalTime().ToString(_Iso8601Format, CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Parse an ISO 8601 string to DateTime.
        /// </summary>
        /// <param name="value">ISO 8601 string.</param>
        /// <returns>DateTime value.</returns>
        internal static DateTime FromIso8601(string value)
        {
            return DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();
        }

        /// <summary>
        /// Parse an ISO 8601 string to nullable DateTime.
        /// </summary>
        /// <param name="value">Object value to parse.</param>
        /// <returns>Nullable DateTime value.</returns>
        internal static DateTime? FromIso8601Nullable(object value)
        {
            if (value == null || value == DBNull.Value) return null;
            string str = value.ToString()!;
            if (string.IsNullOrEmpty(str)) return null;
            return FromIso8601(str);
        }

        /// <summary>
        /// Convert an object value to a nullable string, handling DBNull.
        /// </summary>
        /// <param name="value">Object value.</param>
        /// <returns>String value or null.</returns>
        internal static string? NullableString(object value)
        {
            if (value == null || value == DBNull.Value) return null;
            string str = value.ToString()!;
            return string.IsNullOrEmpty(str) ? null : str;
        }

        /// <summary>
        /// Read a nullable boolean from a SqliteDataReader column.
        /// </summary>
        /// <param name="reader">Data reader.</param>
        /// <param name="column">Column name.</param>
        /// <returns>Nullable boolean value.</returns>
        internal static bool? NullableBool(SqliteDataReader reader, string column)
        {
            try
            {
                object value = reader[column];
                if (value == null || value == DBNull.Value) return null;
                return Convert.ToInt64(value) == 1;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Convert an object value to a nullable int, handling DBNull.
        /// </summary>
        /// <param name="value">Object value.</param>
        /// <returns>Nullable int value.</returns>
        internal static int? NullableInt(object value)
        {
            if (value == null || value == DBNull.Value) return null;
            return Convert.ToInt32(value);
        }

        /// <summary>
        /// Convert an object value to a nullable long, handling DBNull.
        /// </summary>
        /// <param name="value">Object value.</param>
        /// <returns>Nullable long value.</returns>
        internal static long? NullableLong(object value)
        {
            if (value == null || value == DBNull.Value) return null;
            return Convert.ToInt64(value);
        }

        #endregion

        #region Private-Methods

        private void InitializeImplementations()
        {
            _Stored = new StoredDialect(DatabaseTypeEnum.Sqlite, () => new SqliteConnection(_ConnectionString), StoredValues);
            Fleets = new FleetMethods(this, _Settings, _Logging);
            Vessels = new VesselMethods(this, _Settings, _Logging);
            Captains = new CaptainMethods(this, _Settings, _Logging);
            Missions = new MissionMethods(this, _Settings, _Logging);
            Voyages = new VoyageMethods(this, _Settings, _Logging);
            PlanningSessions = new PlanningSessionMethods(this, _Settings, _Logging);
            PlanningSessionMessages = new PlanningSessionMessageMethods(this, _Settings, _Logging);
            CoordinationRooms = new CoordinationRoomMethods(this, _Settings, _Logging);
            CoordinationMessages = new CoordinationMessageMethods(this, _Settings, _Logging);
            CoordinationParticipants = new CoordinationParticipantMethods(this, _Settings, _Logging);
            CoordinationClaims = new CoordinationClaimMethods(this, _Settings, _Logging);
            Objectives = new ObjectiveMethods(this, _Settings, _Logging);
            ObjectiveRefinementSessions = new ObjectiveRefinementSessionMethods(this, _Settings, _Logging);
            ObjectiveRefinementMessages = new ObjectiveRefinementMessageMethods(this, _Settings, _Logging);
            Docks = new DockMethods(this, _Settings, _Logging);
            Signals = new SignalMethods(this, _Settings, _Logging);
            Events = new EventMethods(this, _Settings, _Logging);
            RequestHistory = new RequestHistoryMethods(this, _Settings, _Logging);
            MergeEntries = new MergeEntryMethods(this, _Settings, _Logging);
            LandingJobs = new LandingJobMethods(this, _Settings, _Logging);
            Tenants = new TenantMethods(this, _Settings, _Logging);
            Users = new UserMethods(this, _Settings, _Logging);
            Credentials = new CredentialMethods(this, _Settings, _Logging);
            HarborRunnerEnrollments = new HarborRunnerEnrollmentMethods(this);
            HarborJobs = new Armada.Core.Database.HarborJobMethods(() => new SqliteConnection(_ConnectionString), DatabaseTypeEnum.Sqlite);
            MissionAttemptFacts = new MissionAttemptFactMethods(() => new SqliteConnection(_ConnectionString), DatabaseTypeEnum.Sqlite);
            PreparationClaimObservations = new PreparationClaimObservationMethods(() => new SqliteConnection(_ConnectionString), DatabaseTypeEnum.Sqlite);
            MemoryProposals = new MemoryProposalMethods(() => new SqliteConnection(_ConnectionString), DatabaseTypeEnum.Sqlite);
            LaneStateTransitions = new LaneStateTransitionMethods(() => new SqliteConnection(_ConnectionString), DatabaseTypeEnum.Sqlite);
            DataExpiry = new DataExpiryMethods(() => new SqliteConnection(_ConnectionString), DatabaseTypeEnum.Sqlite);
            PromptTemplates = new PromptTemplateMethods(this, _Settings, _Logging);
            Playbooks = new PlaybookMethods(this, _Settings, _Logging);
            Memories = new MemoryMethods(this, _Settings, _Logging);
            Personas = new PersonaMethods(this, _Settings, _Logging);
            Pipelines = new PipelineMethods(this, _Settings, _Logging);
            WorkflowProfiles = new WorkflowProfileMethods(this, _Settings, _Logging);
            Environments = new DeploymentEnvironmentMethods(this, _Settings, _Logging);
            CheckRuns = new CheckRunMethods(_Stored);
            Releases = new ReleaseMethods(this, _Settings, _Logging);
            Deployments = new DeploymentMethods(_Stored);
            JudgeFollowUps = new JudgeFollowUpMethods(this);
            ProjectProfiles = new ProjectProfileMethods(this, _Settings, _Logging);
            Skills = new SkillMethods(this, _Settings, _Logging);
            CoordinationLeases = new CoordinationLeaseMethods(this, _Settings, _Logging);
            TokenUsage = new TokenUsageMethods(this, _Settings, _Logging);
            ModelEndpoints = new ModelEndpointMethods(this, _Settings, _Logging);
        }

        #endregion
    }
}
