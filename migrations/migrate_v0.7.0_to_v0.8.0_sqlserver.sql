-- SQL Server manual pre-stage for Armada v0.8.0 backlog/objective support.
-- This mirrors automatic startup migration 42.

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'objectives')
CREATE TABLE objectives (
    id NVARCHAR(450) NOT NULL PRIMARY KEY,
    tenant_id NVARCHAR(450),
    user_id NVARCHAR(450),
    title NVARCHAR(450) NOT NULL,
    description NVARCHAR(MAX),
    status NVARCHAR(64) NOT NULL CONSTRAINT DF_objectives_status DEFAULT 'Draft',
    kind NVARCHAR(64) NOT NULL CONSTRAINT DF_objectives_kind DEFAULT 'Feature',
    category NVARCHAR(450),
    priority NVARCHAR(64) NOT NULL CONSTRAINT DF_objectives_priority DEFAULT 'P2',
    rank INT NOT NULL CONSTRAINT DF_objectives_rank DEFAULT 0,
    backlog_state NVARCHAR(64) NOT NULL CONSTRAINT DF_objectives_backlog_state DEFAULT 'Inbox',
    effort NVARCHAR(64) NOT NULL CONSTRAINT DF_objectives_effort DEFAULT 'M',
    owner NVARCHAR(450),
    target_version NVARCHAR(450),
    due_utc NVARCHAR(450),
    parent_objective_id NVARCHAR(450),
    blocked_by_objective_ids_json NVARCHAR(MAX),
    refinement_summary NVARCHAR(MAX),
    suggested_pipeline_id NVARCHAR(450),
    suggested_playbooks_json NVARCHAR(MAX),
    tags_json NVARCHAR(MAX),
    acceptance_criteria_json NVARCHAR(MAX),
    non_goals_json NVARCHAR(MAX),
    rollout_constraints_json NVARCHAR(MAX),
    evidence_links_json NVARCHAR(MAX),
    fleet_ids_json NVARCHAR(MAX),
    vessel_ids_json NVARCHAR(MAX),
    planning_session_ids_json NVARCHAR(MAX),
    refinement_session_ids_json NVARCHAR(MAX),
    voyage_ids_json NVARCHAR(MAX),
    mission_ids_json NVARCHAR(MAX),
    check_run_ids_json NVARCHAR(MAX),
    release_ids_json NVARCHAR(MAX),
    deployment_ids_json NVARCHAR(MAX),
    incident_ids_json NVARCHAR(MAX),
    source_provider NVARCHAR(450),
    source_type NVARCHAR(450),
    source_id NVARCHAR(450),
    source_url NVARCHAR(MAX),
    source_updated_utc NVARCHAR(450),
    created_utc NVARCHAR(450) NOT NULL,
    last_update_utc NVARCHAR(450) NOT NULL,
    completed_utc NVARCHAR(450),
    CONSTRAINT FK_objectives_tenant FOREIGN KEY (tenant_id) REFERENCES tenants(id) ON DELETE CASCADE,
    CONSTRAINT FK_objectives_user FOREIGN KEY (user_id) REFERENCES users(id),
    CONSTRAINT FK_objectives_parent FOREIGN KEY (parent_objective_id) REFERENCES objectives(id),
    CONSTRAINT FK_objectives_pipeline FOREIGN KEY (suggested_pipeline_id) REFERENCES pipelines(id)
);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'idx_objectives_tenant_status_updated')
    CREATE INDEX idx_objectives_tenant_status_updated ON objectives(tenant_id, status, last_update_utc DESC);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'idx_objectives_tenant_backlog_priority_rank')
    CREATE INDEX idx_objectives_tenant_backlog_priority_rank ON objectives(tenant_id, backlog_state, priority, rank);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'idx_objectives_tenant_kind_priority')
    CREATE INDEX idx_objectives_tenant_kind_priority ON objectives(tenant_id, kind, priority);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'idx_objectives_tenant_owner')
    CREATE INDEX idx_objectives_tenant_owner ON objectives(tenant_id, owner);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'idx_objectives_tenant_due')
    CREATE INDEX idx_objectives_tenant_due ON objectives(tenant_id, due_utc);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'idx_objectives_tenant_target_version')
    CREATE INDEX idx_objectives_tenant_target_version ON objectives(tenant_id, target_version);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'idx_objectives_tenant_parent')
    CREATE INDEX idx_objectives_tenant_parent ON objectives(tenant_id, parent_objective_id);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'idx_objectives_tenant_source')
    CREATE INDEX idx_objectives_tenant_source ON objectives(tenant_id, source_provider, source_type, source_id);

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'objective_refinement_sessions')
CREATE TABLE objective_refinement_sessions (
    id NVARCHAR(450) NOT NULL PRIMARY KEY,
    objective_id NVARCHAR(450) NOT NULL,
    tenant_id NVARCHAR(450),
    user_id NVARCHAR(450),
    captain_id NVARCHAR(450) NOT NULL,
    fleet_id NVARCHAR(450),
    vessel_id NVARCHAR(450),
    title NVARCHAR(450) NOT NULL,
    status NVARCHAR(64) NOT NULL CONSTRAINT DF_objective_refinement_sessions_status DEFAULT 'Created',
    process_id INT NULL,
    failure_reason NVARCHAR(MAX),
    created_utc NVARCHAR(450) NOT NULL,
    started_utc NVARCHAR(450),
    completed_utc NVARCHAR(450),
    last_update_utc NVARCHAR(450) NOT NULL,
    CONSTRAINT FK_objective_refinement_sessions_objective FOREIGN KEY (objective_id) REFERENCES objectives(id) ON DELETE CASCADE,
    CONSTRAINT FK_objective_refinement_sessions_tenant FOREIGN KEY (tenant_id) REFERENCES tenants(id),
    CONSTRAINT FK_objective_refinement_sessions_user FOREIGN KEY (user_id) REFERENCES users(id),
    CONSTRAINT FK_objective_refinement_sessions_captain FOREIGN KEY (captain_id) REFERENCES captains(id),
    CONSTRAINT FK_objective_refinement_sessions_fleet FOREIGN KEY (fleet_id) REFERENCES fleets(id),
    CONSTRAINT FK_objective_refinement_sessions_vessel FOREIGN KEY (vessel_id) REFERENCES vessels(id)
);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'idx_objective_refinement_sessions_tenant_objective_created')
    CREATE INDEX idx_objective_refinement_sessions_tenant_objective_created ON objective_refinement_sessions(tenant_id, objective_id, created_utc DESC);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'idx_objective_refinement_sessions_tenant_captain_status')
    CREATE INDEX idx_objective_refinement_sessions_tenant_captain_status ON objective_refinement_sessions(tenant_id, captain_id, status);

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'objective_refinement_messages')
CREATE TABLE objective_refinement_messages (
    id NVARCHAR(450) NOT NULL PRIMARY KEY,
    objective_refinement_session_id NVARCHAR(450) NOT NULL,
    objective_id NVARCHAR(450) NOT NULL,
    tenant_id NVARCHAR(450),
    user_id NVARCHAR(450),
    role NVARCHAR(64) NOT NULL,
    sequence INT NOT NULL,
    content NVARCHAR(MAX) NOT NULL,
    is_selected BIT NOT NULL CONSTRAINT DF_objective_refinement_messages_is_selected DEFAULT 0,
    created_utc NVARCHAR(450) NOT NULL,
    last_update_utc NVARCHAR(450) NOT NULL,
    CONSTRAINT FK_objective_refinement_messages_session FOREIGN KEY (objective_refinement_session_id) REFERENCES objective_refinement_sessions(id) ON DELETE CASCADE,
    CONSTRAINT FK_objective_refinement_messages_objective FOREIGN KEY (objective_id) REFERENCES objectives(id),
    CONSTRAINT FK_objective_refinement_messages_tenant FOREIGN KEY (tenant_id) REFERENCES tenants(id),
    CONSTRAINT FK_objective_refinement_messages_user FOREIGN KEY (user_id) REFERENCES users(id)
);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'idx_objective_refinement_messages_session_sequence')
    CREATE INDEX idx_objective_refinement_messages_session_sequence ON objective_refinement_messages(objective_refinement_session_id, sequence);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'idx_objective_refinement_messages_objective_created')
    CREATE INDEX idx_objective_refinement_messages_objective_created ON objective_refinement_messages(objective_id, created_utc DESC);

IF NOT EXISTS (SELECT 1 FROM schema_migrations WHERE version = 42)
    INSERT INTO schema_migrations (version, description, applied_utc)
    VALUES (42, 'Add normalized objectives backlog tables', SYSUTCDATETIME());
