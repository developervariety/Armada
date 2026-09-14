namespace Armada.Core.Database.Postgresql
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Npgsql;
    using SyslogLogging;
    using Armada.Core.Database.Postgresql.Implementations;
    using Armada.Core.Database.Postgresql.Queries;
    using Armada.Core.Enums;
    using Armada.Core.Models;
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
        /// Initialize the database, running any pending schema migrations.
        /// </summary>
        /// <param name="token">Cancellation token.</param>
        public override async Task InitializeAsync(CancellationToken token = default)
        {
            _Logging.Info(_Header + "initializing database");

            using (NpgsqlConnection conn = new NpgsqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);

                await using (SchemaInitializationLock schemaLock = await SchemaInitializationLock.AcquireAsync(conn, DatabaseTypeEnum.Postgresql, token).ConfigureAwait(false))
                {
                    // Create migration tracking table
                    using (NpgsqlCommand cmd = new NpgsqlCommand())
                    {
                        cmd.Connection = conn;
                        cmd.CommandText = @"CREATE TABLE IF NOT EXISTS schema_migrations (
                            version INTEGER PRIMARY KEY,
                            description TEXT NOT NULL,
                            applied_utc TIMESTAMP NOT NULL
                        );";
                        await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                    }

                    // Get current schema version
                    int currentVersion = 0;
                    using (NpgsqlCommand cmd = new NpgsqlCommand())
                    {
                        cmd.Connection = conn;
                        cmd.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_migrations;";
                        object? result = await cmd.ExecuteScalarAsync(token).ConfigureAwait(false);
                        if (result != null && result != DBNull.Value) currentVersion = Convert.ToInt32(result);
                    }

                    // Apply pending migrations
                    List<SchemaMigration> migrations = TableQueries.GetMigrations();
                    int applied = 0;

                    foreach (SchemaMigration migration in migrations)
                    {
                        if (migration.Version == 52)
                        {
                            await DefaultDatabaseIdentity.EnsureAsync(conn, DatabaseTypeEnum.Postgresql, MigrationCheckpoint, token).ConfigureAwait(false);
                            await ServerSchemaPrerequisites.EnsureAsync(conn, DatabaseTypeEnum.Postgresql, token).ConfigureAwait(false);
                        }
                        if (migration.Version > currentVersion) MigrationCheckpoint?.Invoke(migration.Version, -1);
                        if (migration.Version <= currentVersion) continue;

                        _Logging.Info(_Header + "applying migration v" + migration.Version + ": " + migration.Description);

                        using (NpgsqlTransaction tx = await conn.BeginTransactionAsync(token).ConfigureAwait(false))
                        {
                            if (migration.Version == 89)
                                await ModelEndpointSchemaGuard.EnsureAsync(conn, tx, Armada.Core.Enums.DatabaseTypeEnum.Postgresql, token).ConfigureAwait(false);
                            if (migration.Version == 90)
                                await CaptainModelEndpointSchemaGuard.EnsureAsync(conn, tx, Armada.Core.Enums.DatabaseTypeEnum.Postgresql, false, token).ConfigureAwait(false);
                            if (migration.Version == 91)
                                await HarborRunnerSchemaGuard.EnsureAsync(conn, tx, Armada.Core.Enums.DatabaseTypeEnum.Postgresql, token).ConfigureAwait(false);
                            for (int statementOrdinal = 0; statementOrdinal < migration.Statements.Count; statementOrdinal++)
                            {
                                string sql = migration.Statements[statementOrdinal];
                                using (NpgsqlCommand cmd = new NpgsqlCommand())
                                {
                                    cmd.Connection = conn;
                                    cmd.Transaction = tx;
                                    cmd.CommandText = sql;
                                    if (migration.Version == 84 || migration.Version == 85 || migration.Version == 86 || migration.Version == 88 || (migration.Version == 90 && statementOrdinal == 0))
                                        await AdditiveColumnMigration.ExecuteAsync(conn, tx, Armada.Core.Enums.DatabaseTypeEnum.Postgresql, sql, token).ConfigureAwait(false);
                                    else
                                        await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                                    MigrationCheckpoint?.Invoke(migration.Version, statementOrdinal);
                                }
                            }

                            // Record migration
                            using (NpgsqlCommand cmd = new NpgsqlCommand())
                            {
                                cmd.Connection = conn;
                                cmd.Transaction = tx;
                                cmd.CommandText = "INSERT INTO schema_migrations (version, description, applied_utc) VALUES (@v, @d, @t);";
                                cmd.Parameters.AddWithValue("@v", migration.Version);
                                cmd.Parameters.AddWithValue("@d", migration.Description);
                                cmd.Parameters.AddWithValue("@t", DateTime.UtcNow);
                                await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                            }

                            await tx.CommitAsync(token).ConfigureAwait(false);
                            applied++;
                            MigrationCheckpoint?.Invoke(migration.Version, -2);
                        }
                    }

                    if (applied > 0)
                        _Logging.Info(_Header + "applied " + applied + " migration(s), schema now at v" + migrations[migrations.Count - 1].Version);
                    else
                        _Logging.Info(_Header + "schema is up to date at v" + currentVersion);
                }
            }

            _Logging.Info(_Header + "database initialized successfully");


        }

        /// <inheritdoc />
        public override async Task<T> ExecuteInTransactionAsync<T>(Func<Task<T>> action, CancellationToken token = default)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));

            using (NpgsqlConnection conn = new NpgsqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (NpgsqlTransaction tx = await conn.BeginTransactionAsync(token).ConfigureAwait(false))
                {
                    try
                    {
                        T result = await action().ConfigureAwait(false);
                        await tx.CommitAsync(token).ConfigureAwait(false);
                        return result;
                    }
                    catch
                    {
                        await tx.RollbackAsync(token).ConfigureAwait(false);
                        throw;
                    }
                }
            }
        }

        /// <summary>
        /// Dispose of the database driver.
        /// </summary>
        /// <inheritdoc />
        public override async Task<int> GetSchemaVersionAsync(CancellationToken token = default)
        {
            using (NpgsqlConnection conn = await _DataSource.OpenConnectionAsync(token).ConfigureAwait(false))
            {
                // The table is absent before the first migration runs, and to_regclass answers null
                // rather than throwing, so a fresh database reports version 0 instead of an error.
                using (NpgsqlCommand exists = new NpgsqlCommand("SELECT to_regclass('public.schema_migrations')::text;", conn))
                {
                    object? table = await exists.ExecuteScalarAsync(token).ConfigureAwait(false);
                    if (table == null || table == DBNull.Value) return 0;
                }

                using (NpgsqlCommand cmd = new NpgsqlCommand("SELECT COALESCE(MAX(version), 0) FROM schema_migrations;", conn))
                {
                    object? result = await cmd.ExecuteScalarAsync(token).ConfigureAwait(false);
                    if (result == null || result == DBNull.Value) return 0;
                    return Convert.ToInt32(result);
                }
            }
        }

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

        /// <summary>
        /// Read a required UTC timestamp without shifting it through local time.
        /// Npgsql returns DateTimeKind.Unspecified; ToUniversalTime() would treat that as local.
        /// </summary>
        /// <param name="value">Column value.</param>
        /// <returns>The instant tagged as UTC.</returns>
        internal static DateTime ReadUtc(object value)
        {
            if (value == null || value == DBNull.Value)
                throw new InvalidCastException("UTC timestamp column was null.");
            return DateTime.SpecifyKind(Convert.ToDateTime(value), DateTimeKind.Utc);
        }

        /// <summary>
        /// Read an optional UTC timestamp without shifting it through local time.
        /// </summary>
        /// <param name="value">Column value.</param>
        /// <returns>The instant tagged as UTC, or null.</returns>
        internal static DateTime? ReadUtcNullable(object value)
        {
            if (value == null || value == DBNull.Value) return null;
            return DateTime.SpecifyKind(Convert.ToDateTime(value), DateTimeKind.Utc);
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
            PlanningSessions = new PlanningSessionMethods(this, _Settings, _Logging);
            PlanningSessionMessages = new PlanningSessionMessageMethods(this, _Settings, _Logging);
            CoordinationRooms = new CoordinationRoomMethods(_DataSource);
            CoordinationMessages = new CoordinationMessageMethods(_DataSource);
            CoordinationParticipants = new CoordinationParticipantMethods(_DataSource);
            CoordinationClaims = new CoordinationClaimMethods(_DataSource);
            Objectives = new ObjectiveMethods(this, _Settings, _Logging);
            ObjectiveRefinementSessions = new ObjectiveRefinementSessionMethods(this, _Settings, _Logging);
            ObjectiveRefinementMessages = new ObjectiveRefinementMessageMethods(this, _Settings, _Logging);
            Docks = new DockMethods(this, _Settings, _Logging);
            Signals = new SignalMethods(_DataSource);
            Events = new EventMethods(_DataSource);
            RequestHistory = new RequestHistoryMethods(_DataSource);
            MergeEntries = new MergeEntryMethods(_DataSource);
            LandingJobs = new LandingJobMethods(_DataSource);
            Tenants = new TenantMethods(this, _Settings, _Logging);
            Users = new UserMethods(this, _Settings, _Logging);
            Credentials = new CredentialMethods(this, _Settings, _Logging);
            HarborRunnerEnrollments = new HarborRunnerEnrollmentMethods(_DataSource);
            PromptTemplates = new PromptTemplateMethods(this, _Settings, _Logging);
            Playbooks = new PlaybookMethods(this, _Settings, _Logging);
            Memories = new MemoryMethods(this, _Settings, _Logging);
            Personas = new PersonaMethods(this, _Settings, _Logging);
            Pipelines = new PipelineMethods(this, _Settings, _Logging);
            WorkflowProfiles = new WorkflowProfileMethods(this);
            Environments = new DeploymentEnvironmentMethods(this);
            CheckRuns = new CheckRunMethods(this);
            Releases = new ReleaseMethods(this);
            Deployments = new DeploymentMethods(this);
            VesselPackHints = new VesselPackHintMethods(this, _Settings, _Logging);
            JudgeFollowUps = new JudgeFollowUpMethods(_Settings);
            Jobs = new JobMethods(this, _Settings, _Logging);
            TokenUsage = new TokenUsageMethods(_DataSource);
            ModelEndpoints = new ModelEndpointMethods(this, _Settings, _Logging);
            ProjectProfiles = new ProjectProfileMethods(this);
            Skills = new SkillMethods(this);
            CoordinationLeases = new CoordinationLeaseMethods(this, _Settings, _Logging);
        }

        #endregion
    }
}
