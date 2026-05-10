namespace Armada.Core.Database.Mysql.Queries
{
    /// <summary>
    /// MySQL DDL statements for all Armada tables.
    /// Uses VARCHAR(450) for indexed keys, LONGTEXT for large text,
    /// TINYINT(1) for booleans, DATETIME(6) for microsecond-precision timestamps, INT for integers.
    /// </summary>
    public static class TableQueries
    {
        #region Public-Members

        /// <summary>
        /// DDL for the schema_migrations table.
        /// </summary>
        public static readonly string SchemaMigrations = @"CREATE TABLE IF NOT EXISTS schema_migrations (
            version INT NOT NULL PRIMARY KEY,
            description LONGTEXT NOT NULL,
            applied_utc DATETIME(6) NOT NULL
        );";

        /// <summary>
        /// DDL for the fleets table.
        /// </summary>
        public static readonly string Fleets = @"CREATE TABLE IF NOT EXISTS fleets (
            id VARCHAR(450) NOT NULL PRIMARY KEY,
            name VARCHAR(450) NOT NULL UNIQUE,
            description LONGTEXT,
            active TINYINT(1) NOT NULL DEFAULT 1,
            created_utc DATETIME(6) NOT NULL,
            last_update_utc DATETIME(6) NOT NULL
        );";

        /// <summary>
        /// DDL for the vessels table.
        /// </summary>
        public static readonly string Vessels = @"CREATE TABLE IF NOT EXISTS vessels (
            id VARCHAR(450) NOT NULL PRIMARY KEY,
            fleet_id VARCHAR(450),
            name VARCHAR(450) NOT NULL UNIQUE,
            repo_url LONGTEXT NOT NULL,
            local_path LONGTEXT,
            default_branch VARCHAR(450) NOT NULL DEFAULT 'main',
            working_directory LONGTEXT,
            project_context LONGTEXT,
            style_guide LONGTEXT,
            enable_model_context TINYINT(1) NOT NULL DEFAULT 1,
            model_context LONGTEXT,
            landing_mode TEXT,
            branch_cleanup_policy TEXT,
            allow_concurrent_missions TINYINT(1) NOT NULL DEFAULT 0,
            last_reflection_mission_id LONGTEXT,
            reflection_threshold INT,
            reorganize_threshold INT,
            pack_curate_threshold INT,
            active TINYINT(1) NOT NULL DEFAULT 1,
            created_utc DATETIME(6) NOT NULL,
            last_update_utc DATETIME(6) NOT NULL,
            FOREIGN KEY (fleet_id) REFERENCES fleets(id) ON DELETE SET NULL
        );";

        /// <summary>
        /// DDL for the captains table.
        /// </summary>
        public static readonly string Captains = @"CREATE TABLE IF NOT EXISTS captains (
            id VARCHAR(450) NOT NULL PRIMARY KEY,
            name VARCHAR(450) NOT NULL UNIQUE,
            runtime VARCHAR(450) NOT NULL DEFAULT 'ClaudeCode',
            system_instructions LONGTEXT,
            runtime_options_json LONGTEXT,
            state VARCHAR(450) NOT NULL DEFAULT 'Idle',
            current_mission_id VARCHAR(450),
            current_dock_id VARCHAR(450),
            process_id INT,
            recovery_attempts INT NOT NULL DEFAULT 0,
            last_heartbeat_utc DATETIME(6),
            created_utc DATETIME(6) NOT NULL,
            last_update_utc DATETIME(6) NOT NULL
        );";

        /// <summary>
        /// DDL for the voyages table.
        /// </summary>
        public static readonly string Voyages = @"CREATE TABLE IF NOT EXISTS voyages (
            id VARCHAR(450) NOT NULL PRIMARY KEY,
            title LONGTEXT NOT NULL,
            description LONGTEXT,
            status VARCHAR(450) NOT NULL DEFAULT 'Open',
            auto_push TINYINT(1),
            auto_create_pull_requests TINYINT(1),
            auto_merge_pull_requests TINYINT(1),
            landing_mode TEXT,
            created_utc DATETIME(6) NOT NULL,
            completed_utc DATETIME(6),
            last_update_utc DATETIME(6) NOT NULL
        );";

        /// <summary>
        /// DDL for the missions table.
        /// </summary>
        public static readonly string Missions = @"CREATE TABLE IF NOT EXISTS missions (
            id VARCHAR(450) NOT NULL PRIMARY KEY,
            voyage_id VARCHAR(450),
            vessel_id VARCHAR(450),
            captain_id VARCHAR(450),
            title LONGTEXT NOT NULL,
            description LONGTEXT,
            status VARCHAR(450) NOT NULL DEFAULT 'Pending',
            priority INT NOT NULL DEFAULT 100,
            parent_mission_id VARCHAR(450),
            branch_name VARCHAR(450),
            dock_id VARCHAR(450),
            process_id INT,
            pr_url LONGTEXT,
            commit_hash VARCHAR(450),
            diff_snapshot LONGTEXT,
            agent_output LONGTEXT,
            prestaged_files LONGTEXT,
            preferred_captain_id VARCHAR(450),
            preferred_model VARCHAR(450),
            created_utc DATETIME(6) NOT NULL,
            started_utc DATETIME(6),
            completed_utc DATETIME(6),
            last_update_utc DATETIME(6) NOT NULL,
            FOREIGN KEY (voyage_id) REFERENCES voyages(id) ON DELETE SET NULL,
            FOREIGN KEY (vessel_id) REFERENCES vessels(id) ON DELETE SET NULL,
            FOREIGN KEY (captain_id) REFERENCES captains(id) ON DELETE SET NULL,
            FOREIGN KEY (parent_mission_id) REFERENCES missions(id) ON DELETE SET NULL
        );";

        /// <summary>
        /// DDL for the docks table.
        /// </summary>
        public static readonly string Docks = @"CREATE TABLE IF NOT EXISTS docks (
            id VARCHAR(450) NOT NULL PRIMARY KEY,
            vessel_id VARCHAR(450) NOT NULL,
            captain_id VARCHAR(450),
            worktree_path LONGTEXT,
            branch_name VARCHAR(450),
            active TINYINT(1) NOT NULL DEFAULT 1,
            created_utc DATETIME(6) NOT NULL,
            last_update_utc DATETIME(6) NOT NULL,
            FOREIGN KEY (vessel_id) REFERENCES vessels(id) ON DELETE CASCADE,
            FOREIGN KEY (captain_id) REFERENCES captains(id) ON DELETE SET NULL
        );";

        /// <summary>
        /// DDL for the signals table.
        /// </summary>
        public static readonly string Signals = @"CREATE TABLE IF NOT EXISTS signals (
            id VARCHAR(450) NOT NULL PRIMARY KEY,
            from_captain_id VARCHAR(450),
            to_captain_id VARCHAR(450),
            type VARCHAR(450) NOT NULL DEFAULT 'Nudge',
            payload LONGTEXT,
            `read` TINYINT(1) NOT NULL DEFAULT 0,
            created_utc DATETIME(6) NOT NULL,
            FOREIGN KEY (from_captain_id) REFERENCES captains(id) ON DELETE SET NULL,
            FOREIGN KEY (to_captain_id) REFERENCES captains(id) ON DELETE SET NULL
        );";

        /// <summary>
        /// DDL for the events table.
        /// </summary>
        public static readonly string Events = @"CREATE TABLE IF NOT EXISTS events (
            id VARCHAR(450) NOT NULL PRIMARY KEY,
            event_type VARCHAR(450) NOT NULL,
            entity_type VARCHAR(450),
            entity_id VARCHAR(450),
            captain_id VARCHAR(450),
            mission_id VARCHAR(450),
            vessel_id VARCHAR(450),
            voyage_id VARCHAR(450),
            message LONGTEXT NOT NULL,
            payload LONGTEXT,
            created_utc DATETIME(6) NOT NULL
        );";

        /// <summary>
        /// DDL for the merge_entries table.
        /// </summary>
        public static readonly string MergeEntries = @"CREATE TABLE IF NOT EXISTS merge_entries (
            id VARCHAR(450) NOT NULL PRIMARY KEY,
            mission_id VARCHAR(450),
            vessel_id VARCHAR(450),
            branch_name VARCHAR(450) NOT NULL,
            target_branch VARCHAR(450) NOT NULL DEFAULT 'main',
            status VARCHAR(450) NOT NULL DEFAULT 'Queued',
            priority INT NOT NULL DEFAULT 0,
            batch_id VARCHAR(450),
            test_command LONGTEXT,
            test_output LONGTEXT,
            test_exit_code INT,
            created_utc DATETIME(6) NOT NULL,
            last_update_utc DATETIME(6) NOT NULL,
            test_started_utc DATETIME(6),
            completed_utc DATETIME(6)
        );";

        /// <summary>
        /// DDL for the tenants table.
        /// </summary>
        public static readonly string Tenants = @"CREATE TABLE IF NOT EXISTS tenants (
            id VARCHAR(450) NOT NULL PRIMARY KEY,
            name VARCHAR(450) NOT NULL,
            active TINYINT(1) NOT NULL DEFAULT 1,
            created_utc DATETIME(6) NOT NULL,
            last_update_utc DATETIME(6) NOT NULL
        );";

        /// <summary>
        /// DDL for the users table.
        /// </summary>
        public static readonly string Users = @"CREATE TABLE IF NOT EXISTS users (
            id VARCHAR(450) NOT NULL PRIMARY KEY,
            tenant_id VARCHAR(450) NOT NULL,
            email VARCHAR(450) NOT NULL,
            password_sha256 VARCHAR(450) NOT NULL,
            first_name VARCHAR(450),
            last_name VARCHAR(450),
            is_admin TINYINT(1) NOT NULL DEFAULT 0,
            is_tenant_admin TINYINT(1) NOT NULL DEFAULT 0,
            active TINYINT(1) NOT NULL DEFAULT 1,
            created_utc DATETIME(6) NOT NULL,
            last_update_utc DATETIME(6) NOT NULL,
            FOREIGN KEY (tenant_id) REFERENCES tenants(id) ON DELETE CASCADE
        );";

        /// <summary>
        /// DDL for the credentials table.
        /// </summary>
        public static readonly string Credentials = @"CREATE TABLE IF NOT EXISTS credentials (
            id VARCHAR(450) NOT NULL PRIMARY KEY,
            tenant_id VARCHAR(450) NOT NULL,
            user_id VARCHAR(450) NOT NULL,
            name VARCHAR(450),
            bearer_token VARCHAR(450) NOT NULL UNIQUE,
            active TINYINT(1) NOT NULL DEFAULT 1,
            created_utc DATETIME(6) NOT NULL,
            last_update_utc DATETIME(6) NOT NULL,
            FOREIGN KEY (tenant_id) REFERENCES tenants(id) ON DELETE CASCADE,
            FOREIGN KEY (user_id) REFERENCES users(id) ON DELETE CASCADE
        );";

        /// <summary>
        /// Migration v3 statements for multi-tenant support: ALTER TABLE, backfill, seed, and indexes.
        /// </summary>
        public static readonly string[] MigrationV3Statements = new string[]
        {
            // New tables
            Tenants,
            Users,
            @"CREATE UNIQUE INDEX idx_users_tenant_email ON users(tenant_id, email);",
            @"CREATE INDEX idx_users_tenant ON users(tenant_id);",
            @"CREATE INDEX idx_users_email ON users(email);",
            Credentials,
            @"CREATE INDEX idx_credentials_tenant ON credentials(tenant_id);",
            @"CREATE INDEX idx_credentials_user ON credentials(user_id);",
            @"CREATE INDEX idx_credentials_bearer ON credentials(bearer_token);",
            // Seed default tenant so FK constraints are satisfied for backfill
            @"INSERT IGNORE INTO tenants (id, name, active, created_utc, last_update_utc)
              VALUES ('default', 'Default Tenant', 1, UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));",
            // Add tenant_id to existing tables
            @"ALTER TABLE fleets ADD COLUMN tenant_id VARCHAR(450);",
            @"ALTER TABLE vessels ADD COLUMN tenant_id VARCHAR(450);",
            @"ALTER TABLE captains ADD COLUMN tenant_id VARCHAR(450);",
            @"ALTER TABLE voyages ADD COLUMN tenant_id VARCHAR(450);",
            @"ALTER TABLE missions ADD COLUMN tenant_id VARCHAR(450);",
            @"ALTER TABLE docks ADD COLUMN tenant_id VARCHAR(450);",
            @"ALTER TABLE signals ADD COLUMN tenant_id VARCHAR(450);",
            @"ALTER TABLE events ADD COLUMN tenant_id VARCHAR(450);",
            @"ALTER TABLE merge_entries ADD COLUMN tenant_id VARCHAR(450);",
            // Backfill existing rows with default tenant
            @"UPDATE fleets SET tenant_id = 'default' WHERE tenant_id IS NULL;",
            @"UPDATE vessels SET tenant_id = 'default' WHERE tenant_id IS NULL;",
            @"UPDATE captains SET tenant_id = 'default' WHERE tenant_id IS NULL;",
            @"UPDATE voyages SET tenant_id = 'default' WHERE tenant_id IS NULL;",
            @"UPDATE missions SET tenant_id = 'default' WHERE tenant_id IS NULL;",
            @"UPDATE docks SET tenant_id = 'default' WHERE tenant_id IS NULL;",
            @"UPDATE signals SET tenant_id = 'default' WHERE tenant_id IS NULL;",
            @"UPDATE events SET tenant_id = 'default' WHERE tenant_id IS NULL;",
            @"UPDATE merge_entries SET tenant_id = 'default' WHERE tenant_id IS NULL;",
            // Indexes on new tables
            @"CREATE INDEX idx_tenants_active ON tenants(active);",
            @"CREATE INDEX idx_credentials_tenant_user ON credentials(tenant_id, user_id);",
            @"CREATE INDEX idx_credentials_active ON credentials(active);",
            // Tenant indexes on operational tables
            @"CREATE INDEX idx_fleets_tenant ON fleets(tenant_id);",
            @"CREATE INDEX idx_fleets_tenant_name ON fleets(tenant_id, name);",
            @"CREATE INDEX idx_fleets_created_utc ON fleets(created_utc);",
            @"CREATE INDEX idx_vessels_tenant ON vessels(tenant_id);",
            @"CREATE INDEX idx_vessels_tenant_fleet ON vessels(tenant_id, fleet_id);",
            @"CREATE INDEX idx_vessels_tenant_name ON vessels(tenant_id, name);",
            @"CREATE INDEX idx_vessels_created_utc ON vessels(created_utc);",
            @"CREATE INDEX idx_captains_tenant ON captains(tenant_id);",
            @"CREATE INDEX idx_captains_tenant_state ON captains(tenant_id, state);",
            @"CREATE INDEX idx_captains_created_utc ON captains(created_utc);",
            @"CREATE INDEX idx_missions_tenant ON missions(tenant_id);",
            @"CREATE INDEX idx_missions_tenant_status ON missions(tenant_id, status);",
            @"CREATE INDEX idx_missions_tenant_vessel ON missions(tenant_id, vessel_id);",
            @"CREATE INDEX idx_missions_tenant_voyage ON missions(tenant_id, voyage_id);",
            @"CREATE INDEX idx_missions_tenant_captain ON missions(tenant_id, captain_id);",
            @"CREATE INDEX idx_missions_tenant_status_priority ON missions(tenant_id, status, priority ASC, created_utc ASC);",
            @"CREATE INDEX idx_voyages_tenant ON voyages(tenant_id);",
            @"CREATE INDEX idx_voyages_tenant_status ON voyages(tenant_id, status);",
            @"CREATE INDEX idx_voyages_created_utc ON voyages(created_utc);",
            @"CREATE INDEX idx_docks_tenant ON docks(tenant_id);",
            @"CREATE INDEX idx_docks_tenant_vessel ON docks(tenant_id, vessel_id);",
            @"CREATE INDEX idx_docks_tenant_vessel_available ON docks(tenant_id, vessel_id, active, captain_id);",
            @"CREATE INDEX idx_docks_tenant_captain ON docks(tenant_id, captain_id);",
            @"CREATE INDEX idx_docks_created_utc ON docks(created_utc);",
            @"CREATE INDEX idx_signals_tenant ON signals(tenant_id);",
            @"CREATE INDEX idx_signals_tenant_to_captain ON signals(tenant_id, to_captain_id);",
            @"CREATE INDEX idx_signals_tenant_to_captain_read ON signals(tenant_id, to_captain_id, `read`);",
            @"CREATE INDEX idx_signals_tenant_created ON signals(tenant_id, created_utc DESC);",
            @"CREATE INDEX idx_events_tenant ON events(tenant_id);",
            @"CREATE INDEX idx_events_tenant_type ON events(tenant_id, event_type);",
            @"CREATE INDEX idx_events_tenant_entity ON events(tenant_id, entity_type, entity_id);",
            @"CREATE INDEX idx_events_tenant_vessel ON events(tenant_id, vessel_id);",
            @"CREATE INDEX idx_events_tenant_voyage ON events(tenant_id, voyage_id);",
            @"CREATE INDEX idx_events_tenant_captain ON events(tenant_id, captain_id);",
            @"CREATE INDEX idx_events_tenant_mission ON events(tenant_id, mission_id);",
            @"CREATE INDEX idx_events_tenant_created ON events(tenant_id, created_utc DESC);",
            @"CREATE INDEX idx_merge_entries_tenant ON merge_entries(tenant_id);",
            @"CREATE INDEX idx_merge_entries_tenant_status ON merge_entries(tenant_id, status);",
            @"CREATE INDEX idx_merge_entries_tenant_status_priority ON merge_entries(tenant_id, status, priority ASC, created_utc ASC);",
            @"CREATE INDEX idx_merge_entries_tenant_vessel ON merge_entries(tenant_id, vessel_id);",
            @"CREATE INDEX idx_merge_entries_tenant_mission ON merge_entries(tenant_id, mission_id);"
        };

        /// <summary>
        /// Migration v4 statements for protected resources and user ownership.
        /// </summary>
        public static readonly string[] MigrationV4Statements = new string[]
        {
            @"ALTER TABLE tenants ADD COLUMN is_protected TINYINT(1) NOT NULL DEFAULT 0;",
            @"ALTER TABLE users ADD COLUMN is_protected TINYINT(1) NOT NULL DEFAULT 0;",
            @"ALTER TABLE credentials ADD COLUMN is_protected TINYINT(1) NOT NULL DEFAULT 0;",
            @"UPDATE tenants SET is_protected = 1 WHERE id IN ('default', 'ten_system');",
            @"UPDATE users SET is_protected = 1 WHERE id IN ('default', 'usr_system');",
            @"UPDATE credentials SET is_protected = 1 WHERE user_id IN ('default', 'usr_system');",
            @"ALTER TABLE fleets ADD COLUMN user_id VARCHAR(450);",
            @"ALTER TABLE vessels ADD COLUMN user_id VARCHAR(450);",
            @"ALTER TABLE captains ADD COLUMN user_id VARCHAR(450);",
            @"ALTER TABLE voyages ADD COLUMN user_id VARCHAR(450);",
            @"ALTER TABLE missions ADD COLUMN user_id VARCHAR(450);",
            @"ALTER TABLE docks ADD COLUMN user_id VARCHAR(450);",
            @"ALTER TABLE signals ADD COLUMN user_id VARCHAR(450);",
            @"ALTER TABLE events ADD COLUMN user_id VARCHAR(450);",
            @"ALTER TABLE merge_entries ADD COLUMN user_id VARCHAR(450);",
            @"UPDATE fleets f SET user_id = COALESCE((SELECT u.id FROM users u WHERE u.tenant_id = f.tenant_id ORDER BY u.created_utc LIMIT 1), 'default') WHERE user_id IS NULL;",
            @"UPDATE vessels v SET user_id = COALESCE((SELECT u.id FROM users u WHERE u.tenant_id = v.tenant_id ORDER BY u.created_utc LIMIT 1), 'default') WHERE user_id IS NULL;",
            @"UPDATE captains c SET user_id = COALESCE((SELECT u.id FROM users u WHERE u.tenant_id = c.tenant_id ORDER BY u.created_utc LIMIT 1), 'default') WHERE user_id IS NULL;",
            @"UPDATE voyages v SET user_id = COALESCE((SELECT u.id FROM users u WHERE u.tenant_id = v.tenant_id ORDER BY u.created_utc LIMIT 1), 'default') WHERE user_id IS NULL;",
            @"UPDATE missions m SET user_id = COALESCE((SELECT u.id FROM users u WHERE u.tenant_id = m.tenant_id ORDER BY u.created_utc LIMIT 1), 'default') WHERE user_id IS NULL;",
            @"UPDATE docks d SET user_id = COALESCE((SELECT u.id FROM users u WHERE u.tenant_id = d.tenant_id ORDER BY u.created_utc LIMIT 1), 'default') WHERE user_id IS NULL;",
            @"UPDATE signals s SET user_id = COALESCE((SELECT u.id FROM users u WHERE u.tenant_id = s.tenant_id ORDER BY u.created_utc LIMIT 1), 'default') WHERE user_id IS NULL;",
            @"UPDATE events e SET user_id = COALESCE((SELECT u.id FROM users u WHERE u.tenant_id = e.tenant_id ORDER BY u.created_utc LIMIT 1), 'default') WHERE user_id IS NULL;",
            @"UPDATE merge_entries m SET user_id = COALESCE((SELECT u.id FROM users u WHERE u.tenant_id = m.tenant_id ORDER BY u.created_utc LIMIT 1), 'default') WHERE user_id IS NULL;",
            @"ALTER TABLE fleets ADD CONSTRAINT fk_fleets_user FOREIGN KEY (user_id) REFERENCES users(id);",
            @"ALTER TABLE vessels ADD CONSTRAINT fk_vessels_user FOREIGN KEY (user_id) REFERENCES users(id);",
            @"ALTER TABLE captains ADD CONSTRAINT fk_captains_user FOREIGN KEY (user_id) REFERENCES users(id);",
            @"ALTER TABLE voyages ADD CONSTRAINT fk_voyages_user FOREIGN KEY (user_id) REFERENCES users(id);",
            @"ALTER TABLE missions ADD CONSTRAINT fk_missions_user FOREIGN KEY (user_id) REFERENCES users(id);",
            @"ALTER TABLE docks ADD CONSTRAINT fk_docks_user FOREIGN KEY (user_id) REFERENCES users(id);",
            @"ALTER TABLE signals ADD CONSTRAINT fk_signals_user FOREIGN KEY (user_id) REFERENCES users(id);",
            @"ALTER TABLE events ADD CONSTRAINT fk_events_user FOREIGN KEY (user_id) REFERENCES users(id);",
            @"ALTER TABLE merge_entries ADD CONSTRAINT fk_merge_entries_user FOREIGN KEY (user_id) REFERENCES users(id);",
            @"CREATE INDEX idx_fleets_user ON fleets(user_id);",
            @"CREATE INDEX idx_fleets_tenant_user ON fleets(tenant_id, user_id);",
            @"CREATE INDEX idx_vessels_user ON vessels(user_id);",
            @"CREATE INDEX idx_vessels_tenant_user ON vessels(tenant_id, user_id);",
            @"CREATE INDEX idx_captains_user ON captains(user_id);",
            @"CREATE INDEX idx_captains_tenant_user ON captains(tenant_id, user_id);",
            @"CREATE INDEX idx_voyages_user ON voyages(user_id);",
            @"CREATE INDEX idx_voyages_tenant_user ON voyages(tenant_id, user_id);",
            @"CREATE INDEX idx_missions_user ON missions(user_id);",
            @"CREATE INDEX idx_missions_tenant_user ON missions(tenant_id, user_id);",
            @"CREATE INDEX idx_docks_user ON docks(user_id);",
            @"CREATE INDEX idx_docks_tenant_user ON docks(tenant_id, user_id);",
            @"CREATE INDEX idx_signals_user ON signals(user_id);",
            @"CREATE INDEX idx_signals_tenant_user ON signals(tenant_id, user_id);",
            @"CREATE INDEX idx_events_user ON events(user_id);",
            @"CREATE INDEX idx_events_tenant_user ON events(tenant_id, user_id);",
            @"CREATE INDEX idx_merge_entries_user ON merge_entries(user_id);",
            @"CREATE INDEX idx_merge_entries_tenant_user ON merge_entries(tenant_id, user_id);"
        };

        /// <summary>
        /// Migration v5 statements for operational tenant foreign keys.
        /// </summary>
        public static readonly string[] MigrationV5Statements = new string[]
        {
            @"ALTER TABLE fleets ADD CONSTRAINT fk_fleets_tenant FOREIGN KEY (tenant_id) REFERENCES tenants(id);",
            @"ALTER TABLE vessels ADD CONSTRAINT fk_vessels_tenant FOREIGN KEY (tenant_id) REFERENCES tenants(id);",
            @"ALTER TABLE captains ADD CONSTRAINT fk_captains_tenant FOREIGN KEY (tenant_id) REFERENCES tenants(id);",
            @"ALTER TABLE voyages ADD CONSTRAINT fk_voyages_tenant FOREIGN KEY (tenant_id) REFERENCES tenants(id);",
            @"ALTER TABLE missions ADD CONSTRAINT fk_missions_tenant FOREIGN KEY (tenant_id) REFERENCES tenants(id);",
            @"ALTER TABLE docks ADD CONSTRAINT fk_docks_tenant FOREIGN KEY (tenant_id) REFERENCES tenants(id);",
            @"ALTER TABLE signals ADD CONSTRAINT fk_signals_tenant FOREIGN KEY (tenant_id) REFERENCES tenants(id);",
            @"ALTER TABLE events ADD CONSTRAINT fk_events_tenant FOREIGN KEY (tenant_id) REFERENCES tenants(id);",
            @"ALTER TABLE merge_entries ADD CONSTRAINT fk_merge_entries_tenant FOREIGN KEY (tenant_id) REFERENCES tenants(id);"
        };

        /// <summary>
        /// Migration v6 statements for tenant-scoped admin role support.
        /// </summary>
        public static readonly string[] MigrationV6Statements = new string[]
        {
            @"ALTER TABLE users ADD COLUMN is_tenant_admin TINYINT(1) NOT NULL DEFAULT 0;",
            @"UPDATE users SET is_tenant_admin = 1 WHERE is_admin = 1;"
        };

        /// <summary>
        /// Migration v7 statements for adding model context fields to vessels.
        /// </summary>
        public static readonly string[] MigrationV7Statements = new string[]
        {
            @"ALTER TABLE vessels ADD COLUMN enable_model_context TINYINT(1) NOT NULL DEFAULT 1;",
            @"ALTER TABLE vessels ADD COLUMN model_context LONGTEXT;"
        };

        /// <summary>
        /// Migration v8 statements for adding system_instructions to captains.
        /// </summary>
        public static readonly string[] MigrationV8Statements = new string[]
        {
            @"ALTER TABLE captains ADD COLUMN system_instructions LONGTEXT;"
        };

        /// <summary>
        /// DDL for the prompt_templates table.
        /// </summary>
        public static readonly string PromptTemplates = @"CREATE TABLE IF NOT EXISTS prompt_templates (
            id VARCHAR(450) NOT NULL PRIMARY KEY,
            tenant_id VARCHAR(450),
            name VARCHAR(450) NOT NULL,
            description LONGTEXT,
            category VARCHAR(450) NOT NULL DEFAULT 'mission',
            content LONGTEXT NOT NULL,
            is_built_in TINYINT(1) NOT NULL DEFAULT 0,
            active TINYINT(1) NOT NULL DEFAULT 1,
            created_utc DATETIME(6) NOT NULL,
            last_update_utc DATETIME(6) NOT NULL,
            FOREIGN KEY (tenant_id) REFERENCES tenants(id)
        );";

        /// <summary>
        /// DDL for the personas table.
        /// </summary>
        public static readonly string Personas = @"CREATE TABLE IF NOT EXISTS personas (
            id VARCHAR(450) NOT NULL PRIMARY KEY,
            tenant_id VARCHAR(450),
            name VARCHAR(450) NOT NULL,
            description LONGTEXT,
            prompt_template_name VARCHAR(450) NOT NULL,
            is_built_in TINYINT(1) NOT NULL DEFAULT 0,
            active TINYINT(1) NOT NULL DEFAULT 1,
            created_utc DATETIME(6) NOT NULL,
            last_update_utc DATETIME(6) NOT NULL,
            FOREIGN KEY (tenant_id) REFERENCES tenants(id)
        );";

        /// <summary>
        /// DDL for the pipelines table.
        /// </summary>
        public static readonly string Pipelines = @"CREATE TABLE IF NOT EXISTS pipelines (
            id VARCHAR(450) NOT NULL PRIMARY KEY,
            tenant_id VARCHAR(450),
            name VARCHAR(450) NOT NULL,
            description LONGTEXT,
            is_built_in TINYINT(1) NOT NULL DEFAULT 0,
            active TINYINT(1) NOT NULL DEFAULT 1,
            created_utc DATETIME(6) NOT NULL,
            last_update_utc DATETIME(6) NOT NULL,
            FOREIGN KEY (tenant_id) REFERENCES tenants(id)
        );";

        /// <summary>
        /// DDL for the pipeline_stages table.
        /// </summary>
        public static readonly string PipelineStages = @"CREATE TABLE IF NOT EXISTS pipeline_stages (
            id VARCHAR(450) NOT NULL PRIMARY KEY,
            pipeline_id VARCHAR(450) NOT NULL,
            stage_order INT NOT NULL,
            persona_name VARCHAR(450) NOT NULL,
            is_optional TINYINT(1) NOT NULL DEFAULT 0,
            description LONGTEXT,
            FOREIGN KEY (pipeline_id) REFERENCES pipelines(id) ON DELETE CASCADE
        );";

        /// <summary>
        /// Migration v9 statements for adding the prompt_templates table.
        /// </summary>
        public static readonly string[] MigrationV9Statements = new string[]
        {
            PromptTemplates,
            @"CREATE UNIQUE INDEX idx_prompt_templates_tenant_name ON prompt_templates(tenant_id, name);",
            @"CREATE INDEX idx_prompt_templates_category ON prompt_templates(category);",
            @"CREATE INDEX idx_prompt_templates_active ON prompt_templates(active);"
        };

        /// <summary>
        /// Migration v10 statements for adding the personas table.
        /// </summary>
        public static readonly string[] MigrationV10Statements = new string[]
        {
            Personas,
            @"CREATE UNIQUE INDEX idx_personas_tenant_name ON personas(tenant_id, name);",
            @"CREATE INDEX idx_personas_active ON personas(active);",
            @"CREATE INDEX idx_personas_prompt_template ON personas(prompt_template_name);"
        };

        /// <summary>
        /// Migration v11 statements for adding captain persona fields.
        /// </summary>
        public static readonly string[] MigrationV11Statements = new string[]
        {
            @"ALTER TABLE captains ADD COLUMN allowed_personas LONGTEXT;",
            @"ALTER TABLE captains ADD COLUMN preferred_persona VARCHAR(450);",
            @"CREATE INDEX idx_captains_preferred_persona ON captains(preferred_persona);"
        };

        /// <summary>
        /// Migration v12 statements for adding mission persona and dependency fields.
        /// </summary>
        public static readonly string[] MigrationV12Statements = new string[]
        {
            @"ALTER TABLE missions ADD COLUMN persona VARCHAR(450);",
            @"ALTER TABLE missions ADD COLUMN depends_on_mission_id VARCHAR(450);",
            @"CREATE INDEX idx_missions_persona ON missions(persona);",
            @"CREATE INDEX idx_missions_depends_on ON missions(depends_on_mission_id);"
        };

        /// <summary>
        /// Migration v13 statements for adding pipelines and pipeline_stages tables.
        /// </summary>
        public static readonly string[] MigrationV13Statements = new string[]
        {
            Pipelines,
            PipelineStages,
            @"CREATE UNIQUE INDEX idx_pipelines_tenant_name ON pipelines(tenant_id, name);",
            @"CREATE INDEX idx_pipelines_active ON pipelines(active);",
            @"CREATE INDEX idx_pipeline_stages_pipeline ON pipeline_stages(pipeline_id);",
            @"CREATE UNIQUE INDEX idx_pipeline_stages_order ON pipeline_stages(pipeline_id, stage_order);",
            @"CREATE INDEX idx_pipeline_stages_persona ON pipeline_stages(persona_name);",
            @"ALTER TABLE fleets ADD COLUMN default_pipeline_id VARCHAR(450);",
            @"ALTER TABLE vessels ADD COLUMN default_pipeline_id VARCHAR(450);",
            @"CREATE INDEX idx_fleets_default_pipeline ON fleets(default_pipeline_id);",
            @"CREATE INDEX idx_vessels_default_pipeline ON vessels(default_pipeline_id);"
        };

        /// <summary>
        /// Migration v14 statements for adding failure_reason to missions.
        /// </summary>
        public static readonly string[] MigrationV14Statements = new string[]
        {
            @"ALTER TABLE missions ADD COLUMN failure_reason LONGTEXT;"
        };

        /// <summary>
        /// Migration v15 statements for adding agent_output to missions.
        /// </summary>
        public static readonly string[] MigrationV15Statements = new string[]
        {
            @"ALTER TABLE missions ADD COLUMN agent_output LONGTEXT;"
        };

        /// <summary>
        /// Migration v26 statements for adding model to captains.
        /// </summary>
        public static readonly string[] MigrationV26Statements = new string[]
        {
            @"ALTER TABLE captains ADD COLUMN model TEXT NULL;"
        };

        /// <summary>
        /// Migration v27 statements for adding total_runtime_ms to missions.
        /// </summary>
        public static readonly string[] MigrationV27Statements = new string[]
        {
            @"ALTER TABLE missions ADD COLUMN total_runtime_ms BIGINT NULL;"
        };

        /// <summary>
        /// Migration v28 statements for adding playbooks and mission/voyage associations.
        /// </summary>
        public static readonly string[] MigrationV28Statements = new string[]
        {
            @"CREATE TABLE IF NOT EXISTS playbooks (
                id VARCHAR(450) NOT NULL PRIMARY KEY,
                tenant_id VARCHAR(450),
                user_id VARCHAR(450),
                file_name VARCHAR(450) NOT NULL,
                description LONGTEXT,
                content LONGTEXT NOT NULL,
                active TINYINT(1) NOT NULL DEFAULT 1,
                created_utc DATETIME(6) NOT NULL,
                last_update_utc DATETIME(6) NOT NULL,
                FOREIGN KEY (tenant_id) REFERENCES tenants(id) ON DELETE CASCADE,
                FOREIGN KEY (user_id) REFERENCES users(id) ON DELETE SET NULL
            );",
            @"CREATE UNIQUE INDEX idx_playbooks_tenant_file_name ON playbooks(tenant_id, file_name);",
            @"CREATE INDEX idx_playbooks_tenant ON playbooks(tenant_id);",
            @"CREATE INDEX idx_playbooks_user ON playbooks(user_id);",
            @"CREATE INDEX idx_playbooks_active ON playbooks(active);",
            @"CREATE TABLE IF NOT EXISTS voyage_playbooks (
                voyage_id VARCHAR(450) NOT NULL,
                playbook_id VARCHAR(450) NOT NULL,
                selection_order INT NOT NULL,
                delivery_mode VARCHAR(450) NOT NULL,
                PRIMARY KEY (voyage_id, selection_order),
                FOREIGN KEY (voyage_id) REFERENCES voyages(id) ON DELETE CASCADE,
                FOREIGN KEY (playbook_id) REFERENCES playbooks(id) ON DELETE CASCADE
            );",
            @"CREATE INDEX idx_voyage_playbooks_playbook ON voyage_playbooks(playbook_id);",
            @"CREATE TABLE IF NOT EXISTS mission_playbook_snapshots (
                mission_id VARCHAR(450) NOT NULL,
                selection_order INT NOT NULL,
                playbook_id VARCHAR(450),
                file_name VARCHAR(450) NOT NULL,
                description LONGTEXT,
                content LONGTEXT NOT NULL,
                delivery_mode VARCHAR(450) NOT NULL,
                resolved_path LONGTEXT,
                worktree_relative_path LONGTEXT,
                source_last_update_utc DATETIME(6),
                PRIMARY KEY (mission_id, selection_order),
                FOREIGN KEY (mission_id) REFERENCES missions(id) ON DELETE CASCADE,
                FOREIGN KEY (playbook_id) REFERENCES playbooks(id) ON DELETE SET NULL
            );",
            @"CREATE INDEX idx_mission_playbook_snapshots_playbook ON mission_playbook_snapshots(playbook_id);"
        };

        /// <summary>
        /// Migration v29 statements for adding prestaged_files JSON column to missions.
        /// </summary>
        public static readonly string[] MigrationV29Statements = new string[]
        {
            @"ALTER TABLE missions ADD COLUMN prestaged_files LONGTEXT;"
        };

        /// <summary>
        /// Migration v30 statements for adding protected_paths JSON column to vessels.
        /// </summary>
        public static readonly string[] MigrationV30Statements = new string[]
        {
            @"ALTER TABLE vessels ADD COLUMN protected_paths LONGTEXT;"
        };

        /// <summary>
        /// Migration v31 statements for adding preferred_captain_id and preferred_model columns to missions.
        /// </summary>
        public static readonly string[] MigrationV31Statements = new string[]
        {
            @"ALTER TABLE missions ADD COLUMN preferred_captain_id VARCHAR(450);",
            @"ALTER TABLE missions ADD COLUMN preferred_model VARCHAR(450);"
        };

        /// <summary>
        /// Migration v32 statements for adding auto_land_predicate JSON column to vessels.
        /// </summary>
        public static readonly string[] MigrationV32Statements = new string[]
        {
            @"ALTER TABLE vessels ADD COLUMN auto_land_predicate LONGTEXT;"
        };

        /// <summary>
        /// Migration v33 statements for adding audit columns to merge_entries and calibration counter to vessels.
        /// </summary>
        public static readonly string[] MigrationV33Statements = new string[]
        {
            @"ALTER TABLE merge_entries ADD COLUMN audit_lane TEXT;",
            @"ALTER TABLE merge_entries ADD COLUMN audit_convention_passed TINYINT(1);",
            @"ALTER TABLE merge_entries ADD COLUMN audit_convention_notes LONGTEXT;",
            @"ALTER TABLE merge_entries ADD COLUMN audit_critical_trigger TEXT;",
            @"ALTER TABLE merge_entries ADD COLUMN audit_deep_picked TINYINT(1);",
            @"ALTER TABLE merge_entries ADD COLUMN audit_deep_completed_utc TEXT;",
            @"ALTER TABLE merge_entries ADD COLUMN audit_deep_verdict TEXT;",
            @"ALTER TABLE merge_entries ADD COLUMN audit_deep_notes LONGTEXT;",
            @"ALTER TABLE merge_entries ADD COLUMN audit_deep_recommended_action LONGTEXT;",
            @"ALTER TABLE vessels ADD COLUMN auto_land_calibration_landed_count INT NOT NULL DEFAULT 0;"
        };

        /// <summary>
        /// Migration v34 statements for adding default_playbooks JSON column to vessels.
        /// </summary>
        public static readonly string[] MigrationV34Statements = new string[]
        {
            @"ALTER TABLE vessels ADD COLUMN default_playbooks LONGTEXT;"
        };

        /// <summary>
        /// Migration v35 statements for adding preferred_model column to pipeline_stages.
        /// </summary>
        public static readonly string[] MigrationV35Statements = new string[]
        {
            @"ALTER TABLE pipeline_stages ADD COLUMN preferred_model VARCHAR(450);"
        };

        /// <summary>
        /// Migration v36 statements for adding pr_url and pr_base_branch columns to merge_entries
        /// (PR-fallback path).
        /// </summary>
        public static readonly string[] MigrationV36Statements = new string[]
        {
            @"ALTER TABLE merge_entries ADD COLUMN pr_url LONGTEXT;",
            @"ALTER TABLE merge_entries ADD COLUMN pr_base_branch VARCHAR(450);"
        };

        /// <summary>
        /// Migration v38 statements for adding merge failure classification columns to merge_entries
        /// and recovery-attempt tracking to missions for the auto-recovery handler.
        /// </summary>
        public static readonly string[] MigrationV38Statements = new string[]
        {
            @"ALTER TABLE merge_entries ADD COLUMN merge_failure_class VARCHAR(64);",
            @"ALTER TABLE merge_entries ADD COLUMN conflicted_files LONGTEXT;",
            @"ALTER TABLE merge_entries ADD COLUMN merge_failure_summary VARCHAR(512);",
            @"ALTER TABLE merge_entries ADD COLUMN diff_line_count INT NOT NULL DEFAULT 0;",
            @"ALTER TABLE missions ADD COLUMN recovery_attempts INT NOT NULL DEFAULT 0;",
            @"ALTER TABLE missions ADD COLUMN last_recovery_action_utc DATETIME(6);"
        };

        /// <summary>
        /// Migration v39 statements for adding runtime_options_json to captains.
        /// </summary>
        public static readonly string[] MigrationV39Statements = new string[]
        {
            @"ALTER TABLE captains ADD COLUMN IF NOT EXISTS runtime_options_json LONGTEXT NULL;"
        };

        /// <summary>
        /// Migration v40 statements for adding reflection tracking columns to vessels.
        /// </summary>
        public static readonly string[] MigrationV40Statements = new string[]
        {
            @"ALTER TABLE vessels ADD COLUMN IF NOT EXISTS last_reflection_mission_id LONGTEXT;",
            @"ALTER TABLE vessels ADD COLUMN IF NOT EXISTS reflection_threshold INT;"
        };

        /// <summary>
        /// Migration v41 statements for adding reorganize_threshold to vessels.
        /// </summary>
        public static readonly string[] MigrationV41Statements = new string[]
        {
            @"ALTER TABLE vessels ADD COLUMN IF NOT EXISTS reorganize_threshold INT;"
        };

        /// <summary>
        /// Migration v42 statements for allowing same-order parallel stages in pipeline_stages.
        /// MySQL does not support `IF EXISTS` on `DROP INDEX` or `IF NOT EXISTS` on `CREATE INDEX`,
        /// so each operation is wrapped in an `INFORMATION_SCHEMA.STATISTICS` lookup that builds
        /// the DDL only when needed and runs it via `PREPARE`/`EXECUTE`. Session variables persist
        /// across the separate `ExecuteNonQueryAsync` calls inside the migration transaction.
        /// </summary>
        public static readonly string[] MigrationV42Statements = new string[]
        {
            @"SET @drop_idx_sql = IF((SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'pipeline_stages' AND INDEX_NAME = 'idx_pipeline_stages_order') > 0, 'DROP INDEX idx_pipeline_stages_order ON pipeline_stages', 'SELECT 1');",
            @"PREPARE drop_idx_stmt FROM @drop_idx_sql;",
            @"EXECUTE drop_idx_stmt;",
            @"DEALLOCATE PREPARE drop_idx_stmt;",
            @"SET @create_idx_sql = IF((SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'pipeline_stages' AND INDEX_NAME = 'idx_pipeline_stages_order') = 0, 'CREATE INDEX idx_pipeline_stages_order ON pipeline_stages(pipeline_id, stage_order)', 'SELECT 1');",
            @"PREPARE create_idx_stmt FROM @create_idx_sql;",
            @"EXECUTE create_idx_stmt;",
            @"DEALLOCATE PREPARE create_idx_stmt;"
        };

        /// <summary>
        /// Migration v43 statements for adding the vessel_pack_hints table and pack_curate_threshold column (v2-F1).
        /// MySQL supports `IF NOT EXISTS` on `CREATE TABLE` and on `ALTER TABLE ... ADD COLUMN` (8.0+) but NOT on
        /// `CREATE INDEX`, so the index is wrapped in an `INFORMATION_SCHEMA.STATISTICS` lookup that builds the DDL
        /// only when the index does not already exist and runs it via `PREPARE`/`EXECUTE`. Same pattern as v42.
        /// </summary>
        public static readonly string[] MigrationV43Statements = new string[]
        {
            @"ALTER TABLE vessels ADD COLUMN IF NOT EXISTS pack_curate_threshold INT;",
            @"CREATE TABLE IF NOT EXISTS vessel_pack_hints (
                id VARCHAR(450) NOT NULL PRIMARY KEY,
                vessel_id VARCHAR(450) NOT NULL,
                goal_pattern LONGTEXT NOT NULL,
                must_include LONGTEXT NOT NULL,
                must_exclude LONGTEXT NOT NULL,
                priority INT NOT NULL DEFAULT 0,
                confidence VARCHAR(32) NOT NULL,
                source_mission_ids LONGTEXT NOT NULL,
                justification LONGTEXT,
                active TINYINT(1) NOT NULL DEFAULT 1,
                created_utc DATETIME(6) NOT NULL,
                last_update_utc DATETIME(6) NOT NULL,
                FOREIGN KEY (vessel_id) REFERENCES vessels(id) ON DELETE CASCADE
            );",
            @"SET @create_pack_idx_sql = IF((SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'vessel_pack_hints' AND INDEX_NAME = 'idx_vessel_pack_hints_vessel') = 0, 'CREATE INDEX idx_vessel_pack_hints_vessel ON vessel_pack_hints(vessel_id, active)', 'SELECT 1');",
            @"PREPARE create_pack_idx_stmt FROM @create_pack_idx_sql;",
            @"EXECUTE create_pack_idx_stmt;",
            @"DEALLOCATE PREPARE create_pack_idx_stmt;"
        };

        /// <summary>
        /// Migration v44 statements for adding identity-memory columns to personas and captains
        /// (Reflections v2-F2). MySQL 8.0+ supports `IF NOT EXISTS` on `ALTER TABLE ... ADD COLUMN`,
        /// so this migration is a flat list of additive ALTERs and idempotent on re-run.
        /// </summary>
        public static readonly string[] MigrationV44Statements = new string[]
        {
            @"ALTER TABLE personas ADD COLUMN IF NOT EXISTS default_playbooks LONGTEXT NULL;",
            @"ALTER TABLE personas ADD COLUMN IF NOT EXISTS curate_threshold INT NULL;",
            @"ALTER TABLE personas ADD COLUMN IF NOT EXISTS learned_playbook_id VARCHAR(450) NULL;",
            @"ALTER TABLE captains ADD COLUMN IF NOT EXISTS default_playbooks LONGTEXT NULL;",
            @"ALTER TABLE captains ADD COLUMN IF NOT EXISTS curate_threshold INT NULL;",
            @"ALTER TABLE captains ADD COLUMN IF NOT EXISTS learned_playbook_id VARCHAR(450) NULL;"
        };

        /// <summary>
        /// Migration v45 statements for adding fleet-memory columns to fleets (Reflections v2-F3).
        /// MySQL 8.0+ supports `IF NOT EXISTS` on `ALTER TABLE ... ADD COLUMN`, so this migration
        /// is a flat list of additive ALTERs and idempotent on re-run.
        /// </summary>
        public static readonly string[] MigrationV45Statements = new string[]
        {
            @"ALTER TABLE fleets ADD COLUMN IF NOT EXISTS default_playbooks LONGTEXT NULL;",
            @"ALTER TABLE fleets ADD COLUMN IF NOT EXISTS curate_threshold INT NULL;",
            @"ALTER TABLE fleets ADD COLUMN IF NOT EXISTS learned_playbook_id VARCHAR(450) NULL;"
        };

        /// <summary>
        /// Index DDL statements for all tables.
        /// </summary>
        public static readonly string[] Indexes = new string[]
        {
            "CREATE INDEX idx_vessels_fleet ON vessels(fleet_id);",
            "CREATE INDEX idx_captains_state ON captains(state);",
            "CREATE INDEX idx_missions_voyage ON missions(voyage_id);",
            "CREATE INDEX idx_missions_vessel ON missions(vessel_id);",
            "CREATE INDEX idx_missions_captain ON missions(captain_id);",
            "CREATE INDEX idx_missions_status ON missions(status);",
            "CREATE INDEX idx_missions_status_priority ON missions(status, priority ASC, created_utc ASC);",
            "CREATE INDEX idx_missions_vessel_status ON missions(vessel_id, status);",
            "CREATE INDEX idx_voyages_status ON voyages(status);",
            "CREATE INDEX idx_docks_vessel ON docks(vessel_id);",
            "CREATE INDEX idx_docks_vessel_available ON docks(vessel_id, active, captain_id);",
            "CREATE INDEX idx_signals_to_captain ON signals(to_captain_id);",
            "CREATE INDEX idx_signals_to_captain_read ON signals(to_captain_id, `read`);",
            "CREATE INDEX idx_signals_created ON signals(created_utc DESC);",
            "CREATE INDEX idx_events_type ON events(event_type);",
            "CREATE INDEX idx_events_captain ON events(captain_id);",
            "CREATE INDEX idx_events_mission ON events(mission_id);",
            "CREATE INDEX idx_events_vessel ON events(vessel_id);",
            "CREATE INDEX idx_events_voyage ON events(voyage_id);",
            "CREATE INDEX idx_events_entity ON events(entity_type, entity_id);",
            "CREATE INDEX idx_events_created ON events(created_utc DESC);",
            "CREATE INDEX idx_merge_entries_status ON merge_entries(status);",
            "CREATE INDEX idx_merge_entries_status_priority ON merge_entries(status, priority ASC, created_utc ASC);",
            "CREATE INDEX idx_merge_entries_vessel ON merge_entries(vessel_id);",
            "CREATE INDEX idx_merge_entries_mission ON merge_entries(mission_id);",
            "CREATE INDEX idx_merge_entries_completed ON merge_entries(completed_utc);"
        };

        #endregion
    }
}
