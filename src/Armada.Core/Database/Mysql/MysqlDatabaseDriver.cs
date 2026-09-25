namespace Armada.Core.Database.Mysql
{
    using System;
    using System.Collections.Generic;
    using System.Data;
    using System.Globalization;
    using System.Threading;
    using System.Threading.Tasks;
    using MySqlConnector;
    using SyslogLogging;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Database.Mysql.Implementations;
    using Armada.Core.Database.Mysql.Queries;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Settings;

    /// <summary>
    /// MySQL implementation of the Armada database driver.
    /// Uses MySqlConnector with connection pooling configured from DatabaseSettings.
    /// </summary>
    public class MysqlDatabaseDriver : DatabaseDriver
    {
        #region Public-Members

        #endregion

        #region Internal-Members

        /// <summary>
        /// How this provider's stored values convert to model values. MySQL stores booleans as
        /// TINYINT integers where 1 is true, and timestamps as DATETIME(6) or ISO 8601 text depending on the column.
        /// </summary>
        internal static readonly StoredValueConverter StoredValues = new StoredValueConverter("MySQL", integerBooleans: true);

        /// <summary>
        /// MySQL stored forms: DATETIME(6) unless named here, booleans as TINYINT(1).
        /// </summary>
        internal static readonly StoredValueBinder StoredBinder = new StoredValueBinder(
            "MySQL",
            StoredTimestampEnum.Timestamp,
            new Dictionary<string, StoredTimestampEnum>
            {
                { "captains.quarantine_until_utc", StoredTimestampEnum.ServerRenderedText },
                { "landing_jobs.completed_utc", StoredTimestampEnum.Iso8601Text },
                { "landing_jobs.created_utc", StoredTimestampEnum.Iso8601Text },
                { "landing_jobs.last_update_utc", StoredTimestampEnum.Iso8601Text },
                { "landing_jobs.started_utc", StoredTimestampEnum.Iso8601Text },
                { "merge_entries.audit_deep_completed_utc", StoredTimestampEnum.ServerRenderedText },
            },
            integerBooleans: true,
            integerBooleanColumns: Array.Empty<string>(),
            timestampDbType: DbType.DateTime,
            zonedTimestampDbType: DbType.DateTime);

        #endregion

        #region Private-Members

        private string _Header = "[MysqlDatabaseDriver] ";
        private DatabaseSettings _Settings;
        private string _ConnectionString;
        private StoredDialect _Stored = null!;
        private LoggingModule _Logging;
        private bool _Disposed = false;


        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate the MySQL database driver.
        /// </summary>
        /// <param name="settings">Database settings including connection pooling parameters.</param>
        /// <param name="logging">Logging module.</param>
        public MysqlDatabaseDriver(DatabaseSettings settings, LoggingModule logging)
        {
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
            _ConnectionString = settings.GetConnectionString();

            _Stored = new StoredDialect(DatabaseTypeEnum.Mysql, () => new MySqlConnection(_ConnectionString), StoredValues);
            Fleets = new FleetMethods(_ConnectionString);
            Vessels = new VesselMethods(_ConnectionString);
            Captains = new CaptainMethods(_ConnectionString);
            Missions = new MissionMethods(_ConnectionString);
            Voyages = new VoyageMethods(_ConnectionString);
            PlanningSessions = new PlanningSessionMethods(_ConnectionString);
            PlanningSessionMessages = new PlanningSessionMessageMethods(_ConnectionString);
            CoordinationRooms = new CoordinationRoomMethods(_ConnectionString);
            CoordinationMessages = new CoordinationMessageMethods(_ConnectionString);
            CoordinationParticipants = new CoordinationParticipantMethods(_ConnectionString);
            CoordinationClaims = new CoordinationClaimMethods(_ConnectionString);
            Objectives = new ObjectiveMethods(_ConnectionString, _Logging);
            ObjectiveRefinementSessions = new ObjectiveRefinementSessionMethods(_ConnectionString, _Logging);
            ObjectiveRefinementMessages = new ObjectiveRefinementMessageMethods(_ConnectionString);
            Docks = new DockMethods(_ConnectionString);
            Signals = new SignalMethods(_ConnectionString);
            Events = new EventMethods(_ConnectionString);
            RequestHistory = new RequestHistoryMethods(_Stored);
            MergeEntries = new MergeEntryMethods(_ConnectionString);
            LandingJobs = new LandingJobMethods(_ConnectionString);
            Tenants = new TenantMethods(_ConnectionString);
            Users = new UserMethods(_ConnectionString);
            Credentials = new CredentialMethods(_ConnectionString);
            HarborRunnerEnrollments = new HarborRunnerEnrollmentMethods(_ConnectionString);
            HarborJobs = new Armada.Core.Database.HarborJobMethods(() => new MySqlConnector.MySqlConnection(_ConnectionString), DatabaseTypeEnum.Mysql);
            MissionAttemptFacts = new MissionAttemptFactMethods(() => new MySqlConnector.MySqlConnection(_ConnectionString), DatabaseTypeEnum.Mysql);
            PreparationClaimObservations = new PreparationClaimObservationMethods(() => new MySqlConnector.MySqlConnection(_ConnectionString), DatabaseTypeEnum.Mysql);
            MemoryProposals = new MemoryProposalMethods(() => new MySqlConnector.MySqlConnection(_ConnectionString), DatabaseTypeEnum.Mysql);
            LaneStateTransitions = new LaneStateTransitionMethods(() => new MySqlConnector.MySqlConnection(_ConnectionString), DatabaseTypeEnum.Mysql);
            DataExpiry = new DataExpiryMethods(() => new MySqlConnector.MySqlConnection(_ConnectionString), DatabaseTypeEnum.Mysql);
            PromptTemplates = new PromptTemplateMethods(_ConnectionString);
            Playbooks = new PlaybookMethods(_ConnectionString);
            Memories = new MemoryMethods(_ConnectionString);
            Personas = new PersonaMethods(_ConnectionString);
            Pipelines = new PipelineMethods(_ConnectionString);
            WorkflowProfiles = new WorkflowProfileMethods(_Stored);
            Environments = new DeploymentEnvironmentMethods(_Stored);
            CheckRuns = new CheckRunMethods(_Stored);
            Releases = new ReleaseMethods(_Stored);
            Deployments = new DeploymentMethods(_Stored);
            JudgeFollowUps = new JudgeFollowUpMethods(_ConnectionString);
            ProjectProfiles = new ProjectProfileMethods(_ConnectionString);
            Skills = new SkillMethods(_ConnectionString);
            CoordinationLeases = new CoordinationLeaseMethods(_ConnectionString);
            TokenUsage = new TokenUsageMethods(_Stored);
            ModelEndpoints = new ModelEndpointMethods(_ConnectionString);
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Initialize the database schema by creating the schema_migrations table
        /// and applying all pending migrations using MySQL DDL.
        /// </summary>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Task.</returns>
        public override async Task InitializeAsync(CancellationToken token = default)
        {
            _Logging.Info(_Header + "initializing database");

            using (MySqlConnection conn = await GetConnectionAsync(token).ConfigureAwait(false))
            {
                await using (SchemaInitializationLock schemaLock = await SchemaInitializationLock.AcquireAsync(conn, DatabaseTypeEnum.Mysql, token).ConfigureAwait(false))
                {
                    // Create migration tracking table
                    using (MySqlCommand cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = TableQueries.SchemaMigrations;
                        await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                    }

                    // Apply pending migrations. The ledger read refuses a skipped lower version.
                    List<SchemaMigration> migrations = GetMigrations();
                    int currentVersion = await AppliedMigrationLedger.ReadCurrentVersionAsync(conn, migrations, DatabaseTypeEnum.Mysql, token).ConfigureAwait(false);
                    int applied = 0;

                    MysqlMigrationRunner runner = new MysqlMigrationRunner(conn, MigrationCheckpoint);
                    await runner.InitializeJournalAsync(token).ConfigureAwait(false);
                    foreach (SchemaMigration migration in migrations)
                    {
                        if (migration.Version == 52)
                        {
                            await DefaultDatabaseIdentity.EnsureAsync(conn, DatabaseTypeEnum.Mysql, MigrationCheckpoint, token).ConfigureAwait(false);
                            await runner.EnsurePrerequisitesAsync(token).ConfigureAwait(false);
                        }
                        if (migration.Version > currentVersion) MigrationCheckpoint?.Invoke(migration.Version, -1);
                        if (migration.Version <= currentVersion) continue;
                        _Logging.Info(_Header + "applying migration v" + migration.Version + ": " + migration.Description);
                        if (migration.Version == 80)
                            await ModelEndpointSchemaGuard.EnsureAsync(conn, null, DatabaseTypeEnum.Mysql, token).ConfigureAwait(false);
                        if (migration.Version == 81)
                            await CaptainModelEndpointSchemaGuard.EnsureAsync(conn, null, DatabaseTypeEnum.Mysql, false, token).ConfigureAwait(false);
                        if (migration.Version == 82)
                            await HarborRunnerSchemaGuard.EnsureAsync(conn, null, DatabaseTypeEnum.Mysql, token).ConfigureAwait(false);
                        if (migration.Version == 96)
                            await MemoryProposalSchema.EnsureAsync(conn, null, DatabaseTypeEnum.Mysql, token).ConfigureAwait(false);
                        await runner.ApplyAsync(migration, token).ConfigureAwait(false);
                        applied++;
                    }

                    if (applied > 0)
                        _Logging.Info(_Header + "applied " + applied + " migration(s), schema now at v" + migrations[migrations.Count - 1].Version);
                    else
                        _Logging.Info(_Header + "schema is up to date at v" + currentVersion);

                    // The stored column forms the binder writes must match the migrated schema before any write.
                    await StoredBinderSchemaGuard.EnsureAsync(conn, Armada.Core.Enums.DatabaseTypeEnum.Mysql, token).ConfigureAwait(false);
                }
            }

            _Logging.Info(_Header + "database initialized successfully");


        }

        /// <summary>
        /// Get an open MySQL connection from the connection pool.
        /// </summary>
        /// <param name="token">Cancellation token.</param>
        /// <returns>An open MySqlConnection.</returns>
        public async Task<MySqlConnection> GetConnectionAsync(CancellationToken token = default)
        {
            MySqlConnection conn = new MySqlConnection(_ConnectionString);
            await conn.OpenAsync(token).ConfigureAwait(false);
            return conn;
        }

        /// <summary>
        /// Execute a non-query SQL statement with optional parameters.
        /// </summary>
        /// <param name="sql">SQL statement to execute.</param>
        /// <param name="parameters">Optional parameters as key-value pairs.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Number of rows affected.</returns>
        public async Task<int> ExecuteQueryAsync(
            string sql,
            Dictionary<string, object?>? parameters = null,
            CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(sql)) throw new ArgumentNullException(nameof(sql));

            using (MySqlConnection conn = await GetConnectionAsync(token).ConfigureAwait(false))
            {
                using (MySqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = sql;

                    if (parameters != null)
                    {
                        foreach (KeyValuePair<string, object?> param in parameters)
                        {
                            cmd.Parameters.AddWithValue(param.Key, param.Value ?? DBNull.Value);
                        }
                    }

                    return await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }
        }

        /// <summary>
        /// Execute a scalar SQL query with optional parameters.
        /// </summary>
        /// <param name="sql">SQL query to execute.</param>
        /// <param name="parameters">Optional parameters as key-value pairs.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The first column of the first row in the result set.</returns>
        public async Task<object?> ExecuteScalarAsync(
            string sql,
            Dictionary<string, object?>? parameters = null,
            CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(sql)) throw new ArgumentNullException(nameof(sql));

            using (MySqlConnection conn = await GetConnectionAsync(token).ConfigureAwait(false))
            {
                using (MySqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = sql;

                    if (parameters != null)
                    {
                        foreach (KeyValuePair<string, object?> param in parameters)
                        {
                            cmd.Parameters.AddWithValue(param.Key, param.Value ?? DBNull.Value);
                        }
                    }

                    return await cmd.ExecuteScalarAsync(token).ConfigureAwait(false);
                }
            }
        }

        /// <summary>
        /// Get the current schema version.
        /// </summary>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Current schema version number, or 0 if no migrations have been applied.</returns>
        public override async Task<int> GetSchemaVersionAsync(CancellationToken token = default)
        {
            using (MySqlConnection conn = await GetConnectionAsync(token).ConfigureAwait(false))
            {
                // Check if schema_migrations table exists
                using (MySqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"SELECT COUNT(*) FROM information_schema.tables
                        WHERE table_schema = DATABASE() AND table_name = 'schema_migrations';";
                    object? result = await cmd.ExecuteScalarAsync(token).ConfigureAwait(false);
                    if (result == null || Convert.ToInt32(result) == 0) return 0;
                }

                using (MySqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_migrations;";
                    object? result = await cmd.ExecuteScalarAsync(token).ConfigureAwait(false);
                    if (result != null && result != DBNull.Value) return Convert.ToInt32(result);
                    return 0;
                }
            }
        }

        /// <summary>
        /// Sanitize a string value for safe use in SQL by escaping single quotes.
        /// </summary>
        /// <param name="value">The string to sanitize.</param>
        /// <returns>Sanitized string with single quotes escaped.</returns>
        public static string Sanitize(string value)
        {
            if (string.IsNullOrEmpty(value)) return value;
            return value.Replace("'", "''");
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

        #region Private-Methods

        internal static List<SchemaMigration> GetMigrations()
        {
            List<string> initialStatements = new List<string>
            {
                TableQueries.Fleets,
                TableQueries.Vessels,
                TableQueries.Captains,
                TableQueries.Voyages,
                TableQueries.Missions,
                TableQueries.Docks,
                TableQueries.Signals,
                TableQueries.Events,
                TableQueries.MergeEntries
            };

            foreach (string index in TableQueries.Indexes)
            {
                initialStatements.Add(index);
            }

            return new List<SchemaMigration>
            {
                new SchemaMigration(
                    1,
                    "Initial schema: fleets, vessels, captains, voyages, missions, docks, signals, events, merge_entries",
                    initialStatements.ToArray()
                ),
                new SchemaMigration(
                    2,
                    "Add allow_concurrent_missions to vessels",
                    @"ALTER TABLE vessels ADD COLUMN allow_concurrent_missions TINYINT(1) NOT NULL DEFAULT 0;"
                ),
                new SchemaMigration(
                    3,
                    "Multi-tenant: add tenants, users, credentials tables and tenant_id columns",
                    TableQueries.MigrationV3Statements
                ),
                new SchemaMigration(
                    4,
                    "Protected resources and user ownership",
                    TableQueries.MigrationV4Statements
                ),
                new SchemaMigration(
                    5,
                    "Operational tenant foreign keys",
                    TableQueries.MigrationV5Statements
                ),
                new SchemaMigration(
                    6,
                    "Add tenant admin role to users",
                    TableQueries.MigrationV6Statements
                ),
                new SchemaMigration(
                    7,
                    "Add enable_model_context and model_context to vessels",
                    TableQueries.MigrationV7Statements
                ),
                new SchemaMigration(
                    8,
                    "Add system_instructions to captains",
                    TableQueries.MigrationV8Statements
                ),
                new SchemaMigration(
                    9,
                    "Add prompt_templates table",
                    TableQueries.MigrationV9Statements
                ),
                new SchemaMigration(
                    10,
                    "Add personas table",
                    TableQueries.MigrationV10Statements
                ),
                new SchemaMigration(
                    11,
                    "Add captain persona fields",
                    TableQueries.MigrationV11Statements
                ),
                new SchemaMigration(
                    12,
                    "Add mission persona and dependency fields",
                    TableQueries.MigrationV12Statements
                ),
                new SchemaMigration(
                    13,
                    "Add pipelines and pipeline_stages tables",
                    TableQueries.MigrationV13Statements
                ),
                new SchemaMigration(
                    14,
                    "Add failure_reason to missions",
                    TableQueries.MigrationV14Statements
                ),
                new SchemaMigration(
                    15,
                    "Add agent_output to missions",
                    TableQueries.MigrationV15Statements
                ),
                new SchemaMigration(
                    26,
                    "Add model to captains",
                    TableQueries.MigrationV26Statements
                ),
                new SchemaMigration(
                    27,
                    "Add total_runtime_ms to missions",
                    TableQueries.MigrationV27Statements
                ),
                new SchemaMigration(
                    28,
                    "Add playbooks and mission/voyage playbook associations",
                    TableQueries.MigrationV28Statements
                ),
                new SchemaMigration(
                    29,
                    "Add prestaged_files JSON column to missions",
                    TableQueries.MigrationV29Statements
                ),
                new SchemaMigration(
                    30,
                    "Add protected_paths JSON column to vessels",
                    TableQueries.MigrationV30Statements
                ),
                new SchemaMigration(
                    31,
                    "Add preferred_model column to missions",
                    TableQueries.MigrationV31Statements
                ),
                new SchemaMigration(
                    32,
                    "Add auto_land_predicate JSON column to vessels",
                    TableQueries.MigrationV32Statements
                ),
                new SchemaMigration(
                    33,
                    "Add audit columns to merge_entries and calibration counter to vessels",
                    TableQueries.MigrationV33Statements
                ),
                new SchemaMigration(
                    34,
                    "Add default_playbooks JSON column to vessels",
                    TableQueries.MigrationV34Statements
                ),
                new SchemaMigration(
                    35,
                    "Add preferred_model column to pipeline_stages for per-stage model override",
                    TableQueries.MigrationV35Statements
                ),
                new SchemaMigration(
                    36,
                    "Add pr_url and pr_base_branch columns to merge_entries for PR-fallback path",
                    TableQueries.MigrationV36Statements
                ),
                new SchemaMigration(
                    38,
                    "Add merge failure classification columns and mission recovery attempts",
                    TableQueries.MigrationV38Statements
                ),
                new SchemaMigration(
                    39,
                    "Add runtime_options_json to captains",
                    TableQueries.MigrationV39Statements
                ),
                new SchemaMigration(
                    40,
                    "Add a mission id column and a threshold column to vessels",
                    TableQueries.MigrationV40Statements
                ),
                new SchemaMigration(
                    41,
                    "Add a threshold column to vessels",
                    TableQueries.MigrationV41Statements
                ),
                new SchemaMigration(
                    42,
                    "Allow same-order parallel stages in pipeline_stages",
                    TableQueries.MigrationV42Statements
                ),
                new SchemaMigration(
                    43,
                    "Add a vessel hint table and a threshold column to vessels",
                    TableQueries.MigrationV43Statements
                ),
                new SchemaMigration(
                    44,
                    "Add playbook and threshold columns to personas and captains",
                    TableQueries.MigrationV44Statements
                ),
                new SchemaMigration(
                    45,
                    "Add playbook and threshold columns to fleets",
                    TableQueries.MigrationV45Statements
                ),
                new SchemaMigration(
                    46,
                    "Add pipeline and mission review gates",
                    TableQueries.MigrationV46Statements
                ),
                new SchemaMigration(
                    48,
                    "Add mission assignment state column to missions",
                    TableQueries.MigrationV48Statements
                ),
                new SchemaMigration(
                    49,
                    "Add sibling_repos JSON column to vessels",
                    TableQueries.MigrationV49Statements
                ),
                new SchemaMigration(
                    50,
                    "Add landing retry counter to missions",
                    TableQueries.MigrationV50Statements
                ),
                new SchemaMigration(
                    51,
                    "Add durable landing jobs",
                    TableQueries.MigrationV51Statements
                ),
                new SchemaMigration(
                    52,
                    "Normalize NULL objective tenant_id/user_id to default",
                    TableQueries.MigrationV52Statements
                ),
                new SchemaMigration(
                    53,
                    "Add auto_dispatch_enabled opt-in flag to objectives",
                    TableQueries.MigrationV53Statements
                ),
                new SchemaMigration(
                    54,
                    "Add architect_max_missions_per_voyage column to vessels",
                    TableQueries.MigrationV54Statements
                ),
                new SchemaMigration(
                    55,
                    "Add captain quarantine columns",
                    TableQueries.MigrationV55Statements
                ),
                new SchemaMigration(
                    56,
                    "Add capabilityhint column to missions",
                    TableQueries.MigrationV56Statements
                ),
                new SchemaMigration(
                    57,
                    "Add stage_order to missions so parallel sibling stages form an identifiable group",
                    TableQueries.MigrationV57Statements
                ),
                new SchemaMigration(
                    58,
                    "Add mission_mode column to missions",
                    TableQueries.MigrationV58Statements
                ),
                new SchemaMigration(
                    59,
                    "Add per-captain provider credential columns to captains",
                    TableQueries.MigrationV59Statements
                ),
                new SchemaMigration(
                    60,
                    "Add retry_skip_captain_ids column to missions so in-place judge re-runs route to a different captain without consuming the rescue budget",
                    TableQueries.MigrationV60Statements
                ),
                new SchemaMigration(
                    61,
                    "Add skills directory",
                    TableQueries.MigrationV61Statements
                ),
                new SchemaMigration(
                    62,
                    "Add reasoning_effort to captains",
                    TableQueries.MigrationV62Statements
                ),
                new SchemaMigration(
                    63,
                    "Add redispatch_attempts to missions for no-op completion recovery",
                    TableQueries.MigrationV63Statements
                ),
                new SchemaMigration(
                    64,
                    "Add dock-boundary scanner config to vessels",
                    TableQueries.MigrationV64Statements
                ),
                new SchemaMigration(
                    65,
                    "Add jobs table for background job tracking",
                    TableQueries.MigrationV65Statements
                ),
                new SchemaMigration(
                    66,
                    "Add per-step captain selection (persona default captain, mission requested captain, voyage captain overrides)",
                    TableQueries.MigrationV66Statements
                ),
                new SchemaMigration(
                    67,
                    "Add token_usage table for per-model token accounting",
                    TableQueries.MigrationV67Statements
                ),
                new SchemaMigration(
                    68,
                    "Add project profiles for layered persona resolution",
                    TableQueries.MigrationV68Statements
                ),
                new SchemaMigration(
                    69,
                    "Add coordination leases for distributed locking",
                    TableQueries.MigrationV69Statements
                ),
                new SchemaMigration(
                    70,
                    "Add reasoning_effort to captains, redispatch_attempts to missions, dock-boundary config to vessels",
                    TableQueries.MigrationV70Statements
                ),
                new SchemaMigration(
                    71,
                    "Add last_process_alive_utc to captains",
                    TableQueries.MigrationV71Statements
                ),
                new SchemaMigration(
                    72,
                    "Add start_from_ref to objectives and missions",
                    TableQueries.MigrationV72Statements
                ),
                new SchemaMigration(
                    73,
                    "Add durable Judge follow-ups",
                    TableQueries.MigrationV73Statements
                ),
                new SchemaMigration(
                    74,
                    "Add objective preparation",
                    TableQueries.MigrationV74Statements
                ),
                new SchemaMigration(75, "Persist vessel preview configuration", VesselPreviewSchema.MigrationV75Statements),
                new SchemaMigration(76, "Persist routing metadata and planning provenance", BackendMetadataSchema.MigrationV76Statements),
                new SchemaMigration(77, "Persist bounded dock Git anchors",
                    @"ALTER TABLE docks ADD COLUMN git_anchors_json LONGTEXT CHARACTER SET utf8mb4 NULL;"),
                new SchemaMigration(78, "Add native captain memory", MemorySchema.MigrationV78Statements),
                new SchemaMigration(79, "Persist last admission observations",
                    @"ALTER TABLE missions ADD COLUMN last_admission_json LONGTEXT CHARACTER SET utf8mb4 NULL;",
                    @"ALTER TABLE missions ADD COLUMN admission_revision BIGINT NOT NULL DEFAULT 0;"),
                new SchemaMigration(80, "Persist managed model endpoints", TableQueries.MigrationV80Statements),
                new SchemaMigration(81, "Persist captain model endpoint links", TableQueries.MigrationV81Statements),
                new SchemaMigration(82, "Persist Harbor runner enrollments", TableQueries.MigrationV82Statements),
                new SchemaMigration(83, "Persist project authorization policy", TableQueries.MigrationV83Statements),
                new SchemaMigration(84, "Move terminal objectives out of dispatchable backlog states", TableQueries.MigrationV84Statements),
                new SchemaMigration(85, "Drop the vessel_pack_hints table, threshold and playbook reference columns and the catalog rows they describe", CatalogAndColumnPruneSchema.MysqlStatements),
                new SchemaMigration(86, "Persist mission attempt facts", TableQueries.MigrationV86Statements),
                new SchemaMigration(87, "Persist Check regression links", TableQueries.MigrationV87Statements),
                new SchemaMigration(88, "Persist preparation claim observations", TableQueries.MigrationV88Statements),
                new SchemaMigration(89, "Persist lane state transitions and Check slot requests", TableQueries.MigrationV89Statements),
                new SchemaMigration(90, "Persist configuration record ownership", TableQueries.MigrationV90Statements),
                new SchemaMigration(91, "Persist Harbor job records", TableQueries.MigrationV91Statements),
                new SchemaMigration(92, "Record terminal-voyage reconciliation on the mission row", TableQueries.MigrationV92Statements),
                new SchemaMigration(93, "Delete unreferenced built-in reviewer personas and their templates", ReviewerPersonaPruneSchema.MysqlStatements),
                new SchemaMigration(94, "Cancel missions stored with the WaitingForInput status", MissionInputWaitCancelSchema.MysqlStatements),
                new SchemaMigration(95, "Persist the Judge PASS operator-review hold on missions", MissionOperatorHoldPersistence.MysqlStatements),
                new SchemaMigration(96, "Persist memory proposals", MemoryProposalSchema.MysqlStatements),
                new SchemaMigration(97, "Persist captain preference ranks and persona specialist flags", TierRoutingPersistence.MysqlStatements),
                new SchemaMigration(98, "Drop the unused jobs table", JobsTableDropSchema.MysqlStatements),
                new SchemaMigration(99, "Persist persona minimum capability tiers", PersonaMinimumTierPersistence.MysqlStatements),
                new SchemaMigration(100, "Persist agent process start times next to process identifiers", ProcessLaunchIdentityPersistence.MysqlStatements),
                new SchemaMigration(101, "Persist token usage input buckets and the counting rule, and widen token counts to 64-bit", TokenUsageInputBucketsPersistence.MysqlStatements),
                new SchemaMigration(102, "Keep same-order pipeline stages in their submitted order", PipelineStagePositionPersistence.MysqlStatements),
                new SchemaMigration(103, "Persist planning sessions and their transcript messages", PlanningSessionSchema.MysqlStatements)
            };
        }

        internal static DateTime FromIso8601(string value)
        {
            return DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();
        }

        internal static DateTime? FromIso8601Nullable(object value)
        {
            if (value == null || value == DBNull.Value) return null;
            if (value is DateTime dt) return DateTime.SpecifyKind(dt, DateTimeKind.Utc);
            string str = value.ToString()!;
            if (string.IsNullOrEmpty(str)) return null;
            return FromIso8601(str);
        }

        /// <summary>
        /// Read a required UTC timestamp from a MySQL DATETIME(6) without dropping the fraction
        /// or shifting it through local time. ToString() on a DateTime loses microseconds.
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
        /// Read an optional UTC timestamp from a MySQL DATETIME(6) without dropping the fraction
        /// or shifting it through local time.
        /// </summary>
        /// <param name="value">Column value.</param>
        /// <returns>The instant tagged as UTC, or null.</returns>
        internal static DateTime? ReadUtcNullable(object value)
        {
            if (value == null || value == DBNull.Value) return null;
            return DateTime.SpecifyKind(Convert.ToDateTime(value), DateTimeKind.Utc);
        }

        internal static string? NullableString(object value)
        {
            if (value == null || value == DBNull.Value) return null;
            string str = value.ToString()!;
            return string.IsNullOrEmpty(str) ? null : str;
        }

        internal static int? NullableInt(object value)
        {
            if (value == null || value == DBNull.Value) return null;
            return Convert.ToInt32(value);
        }

        #endregion
    }
}
