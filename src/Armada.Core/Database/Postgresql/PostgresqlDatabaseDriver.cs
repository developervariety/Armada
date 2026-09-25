namespace Armada.Core.Database.Postgresql
{
    using System;
    using System.Collections.Generic;
    using System.Data;
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

        #region Internal-Members

        /// <summary>
        /// Creates the migration ledger.
        /// </summary>
        internal const string SchemaMigrationsTable = @"CREATE TABLE IF NOT EXISTS schema_migrations (
                            version INTEGER PRIMARY KEY,
                            description TEXT NOT NULL,
                            applied_utc TIMESTAMP NOT NULL
                        );";

        /// <summary>
        /// How this provider's stored values convert to model values. PostgreSQL stores booleans as
        /// native booleans, and timestamps as TEXT, TIMESTAMP or TIMESTAMPTZ depending on the column.
        /// </summary>
        internal static readonly StoredValueConverter StoredValues = new StoredValueConverter("PostgreSQL", integerBooleans: false);

        /// <summary>
        /// PostgreSQL stored forms: zone-less TIMESTAMP unless named here, zone-aware TIMESTAMPTZ and two kinds of
        /// text timestamp by column, and two booleans stored as integers.
        /// </summary>
        internal static readonly StoredValueBinder StoredBinder = new StoredValueBinder(
            "PostgreSQL",
            StoredTimestampEnum.Timestamp,
            new Dictionary<string, StoredTimestampEnum>
            {
                { "check_runs.completed_utc", StoredTimestampEnum.TimestampWithZone },
                { "schema_repairs.applied_utc", StoredTimestampEnum.TimestampWithZone },
                { "check_runs.created_utc", StoredTimestampEnum.TimestampWithZone },
                { "check_runs.last_update_utc", StoredTimestampEnum.TimestampWithZone },
                { "check_runs.slot_requested_utc", StoredTimestampEnum.TimestampWithZone },
                { "check_runs.started_utc", StoredTimestampEnum.TimestampWithZone },
                { "coordination_leases.acquired_utc", StoredTimestampEnum.TimestampWithZone },
                { "coordination_leases.expires_utc", StoredTimestampEnum.TimestampWithZone },
                { "deployments.approved_utc", StoredTimestampEnum.TimestampWithZone },
                { "deployments.completed_utc", StoredTimestampEnum.TimestampWithZone },
                { "deployments.created_utc", StoredTimestampEnum.TimestampWithZone },
                { "deployments.last_monitored_utc", StoredTimestampEnum.TimestampWithZone },
                { "deployments.last_regression_alert_utc", StoredTimestampEnum.TimestampWithZone },
                { "deployments.last_update_utc", StoredTimestampEnum.TimestampWithZone },
                { "deployments.monitoring_window_ends_utc", StoredTimestampEnum.TimestampWithZone },
                { "deployments.rolled_back_utc", StoredTimestampEnum.TimestampWithZone },
                { "deployments.started_utc", StoredTimestampEnum.TimestampWithZone },
                { "deployments.verified_utc", StoredTimestampEnum.TimestampWithZone },
                { "environments.created_utc", StoredTimestampEnum.TimestampWithZone },
                { "environments.last_update_utc", StoredTimestampEnum.TimestampWithZone },
                { "harbor_jobs.completed_utc", StoredTimestampEnum.TimestampWithZone },
                { "harbor_jobs.created_utc", StoredTimestampEnum.TimestampWithZone },
                { "harbor_jobs.last_update_utc", StoredTimestampEnum.TimestampWithZone },
                { "harbor_runner_enrollments.created_utc", StoredTimestampEnum.TimestampWithZone },
                { "harbor_runner_enrollments.last_update_utc", StoredTimestampEnum.TimestampWithZone },
                { "harbor_runner_enrollments.revoked_utc", StoredTimestampEnum.TimestampWithZone },
                { "judge_follow_ups.audit_completed_utc", StoredTimestampEnum.TimestampWithZone },
                { "judge_follow_ups.created_utc", StoredTimestampEnum.TimestampWithZone },
                { "judge_follow_ups.last_update_utc", StoredTimestampEnum.TimestampWithZone },
                { "lane_state_transitions.created_utc", StoredTimestampEnum.TimestampWithZone },
                { "memories.created_utc", StoredTimestampEnum.TimestampWithZone },
                { "memories.last_update_utc", StoredTimestampEnum.TimestampWithZone },
                { "memory_proposals.created_utc", StoredTimestampEnum.TimestampWithZone },
                { "memory_proposals.dismissed_utc", StoredTimestampEnum.TimestampWithZone },
                { "memory_proposals.last_update_utc", StoredTimestampEnum.TimestampWithZone },
                { "mission_attempt_facts.created_utc", StoredTimestampEnum.TimestampWithZone },
                { "model_endpoints.created_utc", StoredTimestampEnum.TimestampWithZone },
                { "model_endpoints.last_health_check_utc", StoredTimestampEnum.TimestampWithZone },
                { "model_endpoints.last_update_utc", StoredTimestampEnum.TimestampWithZone },
                { "planning_session_messages.created_utc", StoredTimestampEnum.TimestampWithZone },
                { "planning_session_messages.last_update_utc", StoredTimestampEnum.TimestampWithZone },
                { "planning_sessions.completed_utc", StoredTimestampEnum.TimestampWithZone },
                { "planning_sessions.created_utc", StoredTimestampEnum.TimestampWithZone },
                { "planning_sessions.last_update_utc", StoredTimestampEnum.TimestampWithZone },
                { "planning_sessions.started_utc", StoredTimestampEnum.TimestampWithZone },
                { "preparation_claim_observations.created_utc", StoredTimestampEnum.TimestampWithZone },
                { "releases.created_utc", StoredTimestampEnum.TimestampWithZone },
                { "releases.last_update_utc", StoredTimestampEnum.TimestampWithZone },
                { "releases.published_utc", StoredTimestampEnum.TimestampWithZone },
                { "workflow_profiles.created_utc", StoredTimestampEnum.TimestampWithZone },
                { "workflow_profiles.last_update_utc", StoredTimestampEnum.TimestampWithZone },
                { "captains.quarantine_until_utc", StoredTimestampEnum.ServerRenderedText },
                { "project_profiles.created_utc", StoredTimestampEnum.ServerRenderedText },
                { "project_profiles.last_update_utc", StoredTimestampEnum.ServerRenderedText },
                { "skills.created_utc", StoredTimestampEnum.ServerRenderedText },
                { "skills.last_update_utc", StoredTimestampEnum.ServerRenderedText },
                { "coordination_claims.created_utc", StoredTimestampEnum.Iso8601Text },
                { "coordination_claims.expires_utc", StoredTimestampEnum.Iso8601Text },
                { "coordination_claims.last_update_utc", StoredTimestampEnum.Iso8601Text },
                { "coordination_messages.created_utc", StoredTimestampEnum.Iso8601Text },
                { "coordination_messages.last_update_utc", StoredTimestampEnum.Iso8601Text },
                { "coordination_participants.created_utc", StoredTimestampEnum.Iso8601Text },
                { "coordination_participants.last_seen_utc", StoredTimestampEnum.Iso8601Text },
                { "coordination_participants.last_update_utc", StoredTimestampEnum.Iso8601Text },
                { "coordination_rooms.created_utc", StoredTimestampEnum.Iso8601Text },
                { "coordination_rooms.last_update_utc", StoredTimestampEnum.Iso8601Text },
                { "events.created_utc", StoredTimestampEnum.Iso8601Text },
                { "landing_jobs.completed_utc", StoredTimestampEnum.Iso8601Text },
                { "landing_jobs.created_utc", StoredTimestampEnum.Iso8601Text },
                { "landing_jobs.last_update_utc", StoredTimestampEnum.Iso8601Text },
                { "landing_jobs.started_utc", StoredTimestampEnum.Iso8601Text },
                { "merge_entries.audit_deep_completed_utc", StoredTimestampEnum.Iso8601Text },
                { "merge_entries.completed_utc", StoredTimestampEnum.Iso8601Text },
                { "merge_entries.created_utc", StoredTimestampEnum.Iso8601Text },
                { "merge_entries.last_update_utc", StoredTimestampEnum.Iso8601Text },
                { "merge_entries.test_started_utc", StoredTimestampEnum.Iso8601Text },
                { "request_history.created_utc", StoredTimestampEnum.Iso8601Text },
                { "signals.created_utc", StoredTimestampEnum.Iso8601Text },
                { "token_usage.created_utc", StoredTimestampEnum.Iso8601Text },
            },
            integerBooleans: false,
            integerBooleanColumns: new[] { "token_usage.estimated", "vessels.secret_scan_enabled" },
            timestampDbType: DbType.DateTime2,
            zonedTimestampDbType: DbType.DateTime);

        #endregion

        #region Private-Members

        private string _Header = "[PostgresqlDatabaseDriver] ";
        private DatabaseSettings _Settings;
        private string _ConnectionString;
        private StoredDialect _Stored = null!;
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
                        cmd.CommandText = SchemaMigrationsTable;
                        await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                    }

                    // Apply pending migrations. The ledger read refuses a skipped lower version.
                    List<SchemaMigration> migrations = TableQueries.GetMigrations();
                    int currentVersion = await AppliedMigrationLedger.ReadCurrentVersionAsync(conn, migrations, DatabaseTypeEnum.Postgresql, token).ConfigureAwait(false);
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
                            if (migration.Version == 105)
                                await MemoryProposalSchema.EnsureAsync(conn, tx, Armada.Core.Enums.DatabaseTypeEnum.Postgresql, token).ConfigureAwait(false);
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

                    // The stored column forms the binder writes must match the migrated schema before any write.
                    await StoredBinderSchemaGuard.EnsureAsync(conn, Armada.Core.Enums.DatabaseTypeEnum.Postgresql, token).ConfigureAwait(false);
                }
            }

            _Logging.Info(_Header + "database initialized successfully");


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
        /// Read an optional text column. An empty string reads as null, as on the other providers.
        /// </summary>
        /// <param name="value">Column value.</param>
        /// <returns>The text, or null when the column is null or empty.</returns>
        internal static string? NullableString(object value)
        {
            if (value == null || value == DBNull.Value) return null;
            string text = value.ToString() ?? String.Empty;
            return text.Length == 0 ? null : text;
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
            return ToUtcInstant(value);
        }

        /// <summary>
        /// Read an optional UTC timestamp without shifting it through local time.
        /// </summary>
        /// <param name="value">Column value.</param>
        /// <returns>The instant tagged as UTC, or null.</returns>
        internal static DateTime? ReadUtcNullable(object value)
        {
            if (value == null || value == DBNull.Value) return null;
            return ToUtcInstant(value);
        }

        /// <summary>
        /// Convert a timestamp column value to a UTC instant. A TIMESTAMP column arrives as an unspecified
        /// DateTime that already holds UTC. A TEXT column arrives as a string in any of the forms a writer has
        /// used ("yyyy-MM-ddTHH:mm:ss.fffffffZ", or PostgreSQL's "yyyy-MM-dd HH:mm:ss+00"); an explicit offset
        /// is honoured and a string without one is read as UTC, so the host time zone never changes the value.
        /// </summary>
        /// <param name="value">Non-null column value.</param>
        /// <returns>The instant tagged as UTC.</returns>
        private static DateTime ToUtcInstant(object value)
        {
            if (value is string text)
            {
                return DateTime.SpecifyKind(
                    DateTime.Parse(
                        text,
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal),
                    DateTimeKind.Utc);
            }

            if (value is DateTimeOffset offset) return offset.UtcDateTime;
            return DateTime.SpecifyKind(Convert.ToDateTime(value, System.Globalization.CultureInfo.InvariantCulture), DateTimeKind.Utc);
        }

        #endregion

        #region Private-Methods

        private void InitializeImplementations()
        {
            _Stored = new StoredDialect(DatabaseTypeEnum.Postgresql, () => _DataSource.CreateConnection(), StoredValues);
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
            RequestHistory = new RequestHistoryMethods(_Stored);
            MergeEntries = new MergeEntryMethods(_DataSource);
            LandingJobs = new LandingJobMethods(_DataSource);
            Tenants = new TenantMethods(this, _Settings, _Logging);
            Users = new UserMethods(this, _Settings, _Logging);
            Credentials = new CredentialMethods(this, _Settings, _Logging);
            HarborRunnerEnrollments = new HarborRunnerEnrollmentMethods(_DataSource);
            HarborJobs = new Armada.Core.Database.HarborJobMethods(() => _DataSource.CreateConnection(), DatabaseTypeEnum.Postgresql);
            MissionAttemptFacts = new MissionAttemptFactMethods(() => _DataSource.CreateConnection(), DatabaseTypeEnum.Postgresql);
            PreparationClaimObservations = new PreparationClaimObservationMethods(() => _DataSource.CreateConnection(), DatabaseTypeEnum.Postgresql);
            MemoryProposals = new MemoryProposalMethods(() => _DataSource.CreateConnection(), DatabaseTypeEnum.Postgresql);
            LaneStateTransitions = new LaneStateTransitionMethods(() => _DataSource.CreateConnection(), DatabaseTypeEnum.Postgresql);
            DataExpiry = new DataExpiryMethods(() => _DataSource.CreateConnection(), DatabaseTypeEnum.Postgresql);
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
            JudgeFollowUps = new JudgeFollowUpMethods(_Settings);
            TokenUsage = new TokenUsageMethods(_Stored);
            ModelEndpoints = new ModelEndpointMethods(this, _Settings, _Logging);
            ProjectProfiles = new ProjectProfileMethods(this);
            Skills = new SkillMethods(this);
            CoordinationLeases = new CoordinationLeaseMethods(this, _Settings, _Logging);
        }

        #endregion
    }
}
