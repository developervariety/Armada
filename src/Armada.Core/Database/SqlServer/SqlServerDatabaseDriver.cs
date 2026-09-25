namespace Armada.Core.Database.SqlServer
{
    using System;
    using System.Collections.Generic;
    using System.Data;
    using System.Globalization;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Data.SqlClient;
    using SyslogLogging;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Database.SqlServer.Implementations;
    using Armada.Core.Database.SqlServer.Queries;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Settings;

    /// <summary>
    /// SQL Server implementation of the Armada database driver.
    /// </summary>
    public class SqlServerDatabaseDriver : DatabaseDriver
    {
        #region Public-Members

        /// <summary>
        /// Connection string for the SQL Server database.
        /// </summary>
        internal string ConnectionString
        {
            get { return _ConnectionString; }
        }

        #endregion

        #region Internal-Members

        /// <summary>
        /// How this provider's stored values convert to model values. SQL Server stores booleans as
        /// BIT values, and timestamps as ISO 8601 text or DATETIME2 depending on the column.
        /// </summary>
        internal static readonly StoredValueConverter StoredValues = new StoredValueConverter("SQL Server", integerBooleans: false);

        /// <summary>
        /// SQL Server stored forms: ISO 8601 text unless named here as DATETIME2, booleans as BIT.
        /// </summary>
        internal static readonly StoredValueBinder StoredBinder = new StoredValueBinder(
            "SQL Server",
            StoredTimestampEnum.Iso8601Text,
            new Dictionary<string, StoredTimestampEnum>
            {
                { "coordination_leases.acquired_utc", StoredTimestampEnum.Timestamp },
                { "coordination_leases.expires_utc", StoredTimestampEnum.Timestamp },
                { "harbor_jobs.completed_utc", StoredTimestampEnum.Timestamp },
                { "harbor_jobs.created_utc", StoredTimestampEnum.Timestamp },
                { "harbor_jobs.last_update_utc", StoredTimestampEnum.Timestamp },
                { "harbor_runner_enrollments.created_utc", StoredTimestampEnum.Timestamp },
                { "harbor_runner_enrollments.last_update_utc", StoredTimestampEnum.Timestamp },
                { "harbor_runner_enrollments.revoked_utc", StoredTimestampEnum.Timestamp },
                { "lane_state_transitions.created_utc", StoredTimestampEnum.Timestamp },
                { "memory_proposals.created_utc", StoredTimestampEnum.Timestamp },
                { "memory_proposals.dismissed_utc", StoredTimestampEnum.Timestamp },
                { "memory_proposals.last_update_utc", StoredTimestampEnum.Timestamp },
                { "mission_attempt_facts.created_utc", StoredTimestampEnum.Timestamp },
                { "missions.last_recovery_action_utc", StoredTimestampEnum.Timestamp },
                { "model_endpoints.created_utc", StoredTimestampEnum.Timestamp },
                { "model_endpoints.last_health_check_utc", StoredTimestampEnum.Timestamp },
                { "model_endpoints.last_update_utc", StoredTimestampEnum.Timestamp },
                { "planning_session_messages.created_utc", StoredTimestampEnum.Timestamp },
                { "planning_session_messages.last_update_utc", StoredTimestampEnum.Timestamp },
                { "planning_sessions.completed_utc", StoredTimestampEnum.Timestamp },
                { "planning_sessions.created_utc", StoredTimestampEnum.Timestamp },
                { "schema_migrations.applied_utc", StoredTimestampEnum.Timestamp },
                { "schema_repairs.applied_utc", StoredTimestampEnum.Timestamp },
                { "planning_sessions.last_update_utc", StoredTimestampEnum.Timestamp },
                { "planning_sessions.started_utc", StoredTimestampEnum.Timestamp },
                { "preparation_claim_observations.created_utc", StoredTimestampEnum.Timestamp },
            },
            integerBooleans: false,
            integerBooleanColumns: Array.Empty<string>(),
            timestampDbType: DbType.DateTime2,
            zonedTimestampDbType: DbType.DateTime2);

        #endregion

        #region Private-Members

        private string _Header = "[SqlServerDatabaseDriver] ";
        private DatabaseSettings _Settings;
        private string _ConnectionString;
        private StoredDialect _Stored = null!;
        private LoggingModule _Logging;
        private bool _Disposed = false;


        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate the SQL Server database driver.
        /// </summary>
        /// <param name="settings">Database settings.</param>
        /// <param name="logging">Logging module.</param>
        public SqlServerDatabaseDriver(DatabaseSettings settings, LoggingModule logging)
        {
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
            _ConnectionString = settings.GetConnectionString();

            _Stored = new StoredDialect(DatabaseTypeEnum.SqlServer, () => new SqlConnection(_ConnectionString), StoredValues);
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
            RequestHistory = new RequestHistoryMethods(_Stored);
            MergeEntries = new MergeEntryMethods(this, _Settings, _Logging);
            LandingJobs = new LandingJobMethods(this, _Settings, _Logging);
            Tenants = new TenantMethods(this, _Settings, _Logging);
            Users = new UserMethods(this, _Settings, _Logging);
            Credentials = new CredentialMethods(this, _Settings, _Logging);
            HarborRunnerEnrollments = new HarborRunnerEnrollmentMethods(this);
            HarborJobs = new Armada.Core.Database.HarborJobMethods(() => new SqlConnection(_ConnectionString), DatabaseTypeEnum.SqlServer);
            MissionAttemptFacts = new MissionAttemptFactMethods(() => new SqlConnection(_ConnectionString), DatabaseTypeEnum.SqlServer);
            PreparationClaimObservations = new PreparationClaimObservationMethods(() => new SqlConnection(_ConnectionString), DatabaseTypeEnum.SqlServer);
            MemoryProposals = new MemoryProposalMethods(() => new SqlConnection(_ConnectionString), DatabaseTypeEnum.SqlServer);
            LaneStateTransitions = new LaneStateTransitionMethods(() => new SqlConnection(_ConnectionString), DatabaseTypeEnum.SqlServer);
            DataExpiry = new DataExpiryMethods(() => new SqlConnection(_ConnectionString), DatabaseTypeEnum.SqlServer);
            PromptTemplates = new PromptTemplateMethods(this, _Settings, _Logging);
            Playbooks = new PlaybookMethods(this, _Settings, _Logging);
            Memories = new MemoryMethods(this, _Settings, _Logging);
            Personas = new PersonaMethods(this, _Settings, _Logging);
            Pipelines = new PipelineMethods(this, _Settings, _Logging);
            WorkflowProfiles = new WorkflowProfileMethods(_Stored);
            Environments = new DeploymentEnvironmentMethods(_Stored);
            CheckRuns = new CheckRunMethods(_Stored);
            Releases = new ReleaseMethods(_Stored);
            Deployments = new DeploymentMethods(_Stored);
            JudgeFollowUps = new JudgeFollowUpMethods(this);
            ProjectProfiles = new ProjectProfileMethods(this);
            Skills = new SkillMethods(this);
            CoordinationLeases = new CoordinationLeaseMethods(this, _Settings, _Logging);
            TokenUsage = new TokenUsageMethods(_Stored);
            ModelEndpoints = new ModelEndpointMethods(this, _Settings, _Logging);
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

            using (SqlConnection conn = new SqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);

                await using (SchemaInitializationLock schemaLock = await SchemaInitializationLock.AcquireAsync(conn, DatabaseTypeEnum.SqlServer, token).ConfigureAwait(false))
                {
                    // Create migration tracking table
                    using (SqlCommand cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = TableQueries.SchemaMigrations;
                        await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                    }

                    // Apply pending migrations. The ledger read refuses a skipped lower version.
                    List<SchemaMigration> migrations = TableQueries.GetMigrations();
                    int currentVersion = await AppliedMigrationLedger.ReadCurrentVersionAsync(conn, migrations, DatabaseTypeEnum.SqlServer, token).ConfigureAwait(false);
                    int applied = 0;

                    foreach (SchemaMigration migration in migrations)
                    {
                        if (migration.Version == 52)
                        {
                            await DefaultDatabaseIdentity.EnsureAsync(conn, DatabaseTypeEnum.SqlServer, MigrationCheckpoint, token).ConfigureAwait(false);
                            await ServerSchemaPrerequisites.EnsureAsync(conn, DatabaseTypeEnum.SqlServer, token).ConfigureAwait(false);
                        }
                        if (migration.Version > currentVersion) MigrationCheckpoint?.Invoke(migration.Version, -1);
                        if (migration.Version <= currentVersion) continue;

                        _Logging.Info(_Header + "applying migration v" + migration.Version + ": " + migration.Description);

                        using (SqlTransaction tx = (SqlTransaction)await conn.BeginTransactionAsync(token).ConfigureAwait(false))
                        {
                            if (migration.Version == 83)
                                await ModelEndpointSchemaGuard.EnsureAsync(conn, tx, DatabaseTypeEnum.SqlServer, token).ConfigureAwait(false);
                            if (migration.Version == 84)
                                await CaptainModelEndpointSchemaGuard.EnsureAsync(conn, tx, DatabaseTypeEnum.SqlServer, false, token).ConfigureAwait(false);
                            if (migration.Version == 85)
                                await HarborRunnerSchemaGuard.EnsureAsync(conn, tx, DatabaseTypeEnum.SqlServer, token).ConfigureAwait(false);
                            if (migration.Version == 99)
                                await MemoryProposalSchema.EnsureAsync(conn, tx, DatabaseTypeEnum.SqlServer, token).ConfigureAwait(false);
                            for (int statementOrdinal = 0; statementOrdinal < migration.Statements.Count; statementOrdinal++)
                            {
                                string sql = migration.Statements[statementOrdinal];
                                using (SqlCommand cmd = conn.CreateCommand())
                                {
                                    cmd.Transaction = tx;
                                    if (migration.Version == 78 || migration.Version == 79 || migration.Version == 80 || migration.Version == 82 || (migration.Version == 84 && statementOrdinal == 0))
                                        await AdditiveColumnMigration.ExecuteAsync(conn, tx, DatabaseTypeEnum.SqlServer, sql, token).ConfigureAwait(false);
                                    else
                                    {
                                        await HistoricalMigrationCorrections.ExecuteSqlServerAsync(cmd, migration.Version, sql, token).ConfigureAwait(false);
                                        await HistoricalMigrationCorrections.RecordSqlServerAsync(conn, tx, migration.Version,
                                            statementOrdinal, sql, cmd.CommandText, token).ConfigureAwait(false);
                                    }
                                    MigrationCheckpoint?.Invoke(migration.Version, statementOrdinal);
                                }
                            }

                            // Record migration
                            using (SqlCommand cmd = conn.CreateCommand())
                            {
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

                    // The stored column forms the binder writes must match the migrated schema before any write.
                    await StoredBinderSchemaGuard.EnsureAsync(conn, Armada.Core.Enums.DatabaseTypeEnum.SqlServer, token).ConfigureAwait(false);
                }
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
            using (SqlConnection conn = new SqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);

                // Check if schema_migrations table exists
                using (SqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES
                        WHERE TABLE_NAME = 'schema_migrations';";
                    object? result = await cmd.ExecuteScalarAsync(token).ConfigureAwait(false);
                    if (result == null || Convert.ToInt32(result) == 0) return 0;
                }

                using (SqlCommand cmd = conn.CreateCommand())
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
        /// Read a SQL Server timestamp without formatting a typed DateTime through a culture-sensitive,
        /// second-resolution string first.
        /// </summary>
        /// <param name="value">Database timestamp.</param>
        /// <returns>UTC timestamp.</returns>
        internal static DateTime FromDatabaseTimestamp(object value)
        {
            if (value is DateTime timestamp)
                return DateTime.SpecifyKind(timestamp, DateTimeKind.Utc);
            return FromIso8601(value.ToString()!);
        }

        /// <summary>
        /// Read an optional SQL Server timestamp while preserving DateTime2 fractional seconds.
        /// </summary>
        /// <param name="value">Database timestamp.</param>
        /// <returns>UTC timestamp, or null.</returns>
        internal static DateTime? FromDatabaseTimestampNullable(object value)
        {
            if (value == null || value == DBNull.Value) return null;
            return FromDatabaseTimestamp(value);
        }

        /// <summary>
        /// Convert an object value to a nullable DateTime with UTC kind, handling DBNull.
        /// </summary>
        /// <param name="value">Object value.</param>
        /// <returns>DateTime value or null.</returns>
        internal static DateTime? NullableDateTime(object value)
        {
            if (value == null || value == DBNull.Value) return null;
            if (value is DateTime dt) return DateTime.SpecifyKind(dt, DateTimeKind.Utc);
            return DateTime.SpecifyKind(Convert.ToDateTime(value, System.Globalization.CultureInfo.InvariantCulture), DateTimeKind.Utc);
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
        /// Read a nullable boolean from a SqlDataReader column.
        /// </summary>
        /// <param name="reader">Data reader.</param>
        /// <param name="column">Column name.</param>
        /// <returns>Nullable boolean value.</returns>
        internal static bool? NullableBool(SqlDataReader reader, string column)
        {
            try
            {
                object value = reader[column];
                if (value == null || value == DBNull.Value) return null;
                return Convert.ToBoolean(value);
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

        #endregion

    }
}
