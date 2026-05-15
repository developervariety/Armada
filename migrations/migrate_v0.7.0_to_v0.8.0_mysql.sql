-- MySQL manual pre-stage for Armada v0.8.0 backlog/objective support.
-- This mirrors automatic startup migration 42.
-- Validate whether each index already exists before re-running manually.

CREATE TABLE IF NOT EXISTS objectives (
    id VARCHAR(450) NOT NULL PRIMARY KEY,
    tenant_id VARCHAR(450),
    user_id VARCHAR(450),
    title VARCHAR(450) NOT NULL,
    description LONGTEXT,
    status VARCHAR(64) NOT NULL DEFAULT 'Draft',
    kind VARCHAR(64) NOT NULL DEFAULT 'Feature',
    category VARCHAR(450),
    priority VARCHAR(64) NOT NULL DEFAULT 'P2',
    `rank` INT NOT NULL DEFAULT 0,
    backlog_state VARCHAR(64) NOT NULL DEFAULT 'Inbox',
    effort VARCHAR(64) NOT NULL DEFAULT 'M',
    owner VARCHAR(450),
    target_version VARCHAR(450),
    due_utc DATETIME(6) NULL,
    parent_objective_id VARCHAR(450),
    blocked_by_objective_ids_json LONGTEXT,
    refinement_summary LONGTEXT,
    suggested_pipeline_id VARCHAR(450),
    suggested_playbooks_json LONGTEXT,
    tags_json LONGTEXT,
    acceptance_criteria_json LONGTEXT,
    non_goals_json LONGTEXT,
    rollout_constraints_json LONGTEXT,
    evidence_links_json LONGTEXT,
    fleet_ids_json LONGTEXT,
    vessel_ids_json LONGTEXT,
    planning_session_ids_json LONGTEXT,
    refinement_session_ids_json LONGTEXT,
    voyage_ids_json LONGTEXT,
    mission_ids_json LONGTEXT,
    check_run_ids_json LONGTEXT,
    release_ids_json LONGTEXT,
    deployment_ids_json LONGTEXT,
    incident_ids_json LONGTEXT,
    source_provider VARCHAR(450),
    source_type VARCHAR(450),
    source_id VARCHAR(450),
    source_url LONGTEXT,
    source_updated_utc DATETIME(6) NULL,
    created_utc DATETIME(6) NOT NULL,
    last_update_utc DATETIME(6) NOT NULL,
    completed_utc DATETIME(6) NULL,
    FOREIGN KEY (tenant_id) REFERENCES tenants(id) ON DELETE CASCADE,
    FOREIGN KEY (user_id) REFERENCES users(id) ON DELETE SET NULL,
    FOREIGN KEY (parent_objective_id) REFERENCES objectives(id) ON DELETE SET NULL,
    FOREIGN KEY (suggested_pipeline_id) REFERENCES pipelines(id) ON DELETE SET NULL
);

CREATE INDEX idx_objectives_tenant_status_updated ON objectives(tenant_id, status, last_update_utc DESC);
CREATE INDEX idx_objectives_tenant_backlog_priority_rank ON objectives(tenant_id, backlog_state, priority, `rank`);
CREATE INDEX idx_objectives_tenant_kind_priority ON objectives(tenant_id, kind, priority);
CREATE INDEX idx_objectives_tenant_owner ON objectives(tenant_id(128), owner(128));
CREATE INDEX idx_objectives_tenant_due ON objectives(tenant_id, due_utc);
CREATE INDEX idx_objectives_tenant_target_version ON objectives(tenant_id(128), target_version(128));
CREATE INDEX idx_objectives_tenant_parent ON objectives(tenant_id(128), parent_objective_id(128));
CREATE INDEX idx_objectives_tenant_source ON objectives(tenant_id(128), source_provider(64), source_type(64), source_id(128));

CREATE TABLE IF NOT EXISTS objective_refinement_sessions (
    id VARCHAR(450) NOT NULL PRIMARY KEY,
    objective_id VARCHAR(450) NOT NULL,
    tenant_id VARCHAR(450),
    user_id VARCHAR(450),
    captain_id VARCHAR(450),
    fleet_id VARCHAR(450),
    vessel_id VARCHAR(450),
    title VARCHAR(450) NOT NULL,
    status VARCHAR(64) NOT NULL DEFAULT 'Created',
    process_id INT NULL,
    failure_reason LONGTEXT,
    created_utc DATETIME(6) NOT NULL,
    started_utc DATETIME(6) NULL,
    completed_utc DATETIME(6) NULL,
    last_update_utc DATETIME(6) NOT NULL,
    FOREIGN KEY (objective_id) REFERENCES objectives(id) ON DELETE CASCADE,
    FOREIGN KEY (tenant_id) REFERENCES tenants(id) ON DELETE CASCADE,
    FOREIGN KEY (user_id) REFERENCES users(id) ON DELETE SET NULL,
    FOREIGN KEY (captain_id) REFERENCES captains(id) ON DELETE SET NULL,
    FOREIGN KEY (fleet_id) REFERENCES fleets(id) ON DELETE SET NULL,
    FOREIGN KEY (vessel_id) REFERENCES vessels(id) ON DELETE SET NULL
);

CREATE INDEX idx_objective_refinement_sessions_tenant_objective_created
    ON objective_refinement_sessions(tenant_id(128), objective_id(128), created_utc DESC);
CREATE INDEX idx_objective_refinement_sessions_tenant_captain_status
    ON objective_refinement_sessions(tenant_id(128), captain_id(128), status);

CREATE TABLE IF NOT EXISTS objective_refinement_messages (
    id VARCHAR(450) NOT NULL PRIMARY KEY,
    objective_refinement_session_id VARCHAR(450) NOT NULL,
    objective_id VARCHAR(450) NOT NULL,
    tenant_id VARCHAR(450),
    user_id VARCHAR(450),
    role VARCHAR(64) NOT NULL,
    sequence INT NOT NULL,
    content LONGTEXT NOT NULL,
    is_selected TINYINT(1) NOT NULL DEFAULT 0,
    created_utc DATETIME(6) NOT NULL,
    last_update_utc DATETIME(6) NOT NULL,
    FOREIGN KEY (objective_refinement_session_id) REFERENCES objective_refinement_sessions(id) ON DELETE CASCADE,
    FOREIGN KEY (objective_id) REFERENCES objectives(id) ON DELETE CASCADE,
    FOREIGN KEY (tenant_id) REFERENCES tenants(id) ON DELETE CASCADE,
    FOREIGN KEY (user_id) REFERENCES users(id) ON DELETE SET NULL
);

CREATE INDEX idx_objective_refinement_messages_session_sequence
    ON objective_refinement_messages(objective_refinement_session_id, sequence);
CREATE INDEX idx_objective_refinement_messages_objective_created
    ON objective_refinement_messages(objective_id, created_utc DESC);

INSERT IGNORE INTO schema_migrations (version, description, applied_utc)
VALUES (42, 'Add normalized objectives backlog tables', UTC_TIMESTAMP(6));
