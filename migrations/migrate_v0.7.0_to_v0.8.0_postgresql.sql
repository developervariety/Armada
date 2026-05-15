-- PostgreSQL manual pre-stage for Armada v0.8.0 backlog/objective support.
-- This mirrors automatic startup migration 42.

CREATE TABLE IF NOT EXISTS objectives (
    id TEXT PRIMARY KEY,
    tenant_id TEXT,
    user_id TEXT,
    title TEXT NOT NULL,
    description TEXT,
    status TEXT NOT NULL DEFAULT 'Draft',
    kind TEXT NOT NULL DEFAULT 'Feature',
    category TEXT,
    priority TEXT NOT NULL DEFAULT 'P2',
    rank INTEGER NOT NULL DEFAULT 0,
    backlog_state TEXT NOT NULL DEFAULT 'Inbox',
    effort TEXT NOT NULL DEFAULT 'M',
    owner TEXT,
    target_version TEXT,
    due_utc TIMESTAMP,
    parent_objective_id TEXT,
    blocked_by_objective_ids_json TEXT,
    refinement_summary TEXT,
    suggested_pipeline_id TEXT,
    suggested_playbooks_json TEXT,
    tags_json TEXT,
    acceptance_criteria_json TEXT,
    non_goals_json TEXT,
    rollout_constraints_json TEXT,
    evidence_links_json TEXT,
    fleet_ids_json TEXT,
    vessel_ids_json TEXT,
    planning_session_ids_json TEXT,
    refinement_session_ids_json TEXT,
    voyage_ids_json TEXT,
    mission_ids_json TEXT,
    check_run_ids_json TEXT,
    release_ids_json TEXT,
    deployment_ids_json TEXT,
    incident_ids_json TEXT,
    source_provider TEXT,
    source_type TEXT,
    source_id TEXT,
    source_url TEXT,
    source_updated_utc TIMESTAMP,
    created_utc TIMESTAMP NOT NULL,
    last_update_utc TIMESTAMP NOT NULL,
    completed_utc TIMESTAMP,
    FOREIGN KEY (tenant_id) REFERENCES tenants(id) ON DELETE CASCADE,
    FOREIGN KEY (user_id) REFERENCES users(id) ON DELETE SET NULL,
    FOREIGN KEY (parent_objective_id) REFERENCES objectives(id) ON DELETE SET NULL,
    FOREIGN KEY (suggested_pipeline_id) REFERENCES pipelines(id) ON DELETE SET NULL
);

CREATE INDEX IF NOT EXISTS idx_objectives_tenant_status_updated ON objectives(tenant_id, status, last_update_utc DESC);
CREATE INDEX IF NOT EXISTS idx_objectives_tenant_backlog_priority_rank ON objectives(tenant_id, backlog_state, priority, rank);
CREATE INDEX IF NOT EXISTS idx_objectives_tenant_kind_priority ON objectives(tenant_id, kind, priority);
CREATE INDEX IF NOT EXISTS idx_objectives_tenant_owner ON objectives(tenant_id, owner);
CREATE INDEX IF NOT EXISTS idx_objectives_tenant_due ON objectives(tenant_id, due_utc);
CREATE INDEX IF NOT EXISTS idx_objectives_tenant_target_version ON objectives(tenant_id, target_version);
CREATE INDEX IF NOT EXISTS idx_objectives_tenant_parent ON objectives(tenant_id, parent_objective_id);
CREATE INDEX IF NOT EXISTS idx_objectives_tenant_source ON objectives(tenant_id, source_provider, source_type, source_id);

CREATE TABLE IF NOT EXISTS objective_refinement_sessions (
    id TEXT PRIMARY KEY,
    objective_id TEXT NOT NULL,
    tenant_id TEXT,
    user_id TEXT,
    captain_id TEXT NOT NULL,
    fleet_id TEXT,
    vessel_id TEXT,
    title TEXT NOT NULL,
    status TEXT NOT NULL DEFAULT 'Created',
    process_id INTEGER,
    failure_reason TEXT,
    created_utc TIMESTAMP NOT NULL,
    started_utc TIMESTAMP,
    completed_utc TIMESTAMP,
    last_update_utc TIMESTAMP NOT NULL,
    FOREIGN KEY (objective_id) REFERENCES objectives(id) ON DELETE CASCADE,
    FOREIGN KEY (tenant_id) REFERENCES tenants(id) ON DELETE CASCADE,
    FOREIGN KEY (user_id) REFERENCES users(id) ON DELETE SET NULL,
    FOREIGN KEY (captain_id) REFERENCES captains(id) ON DELETE SET NULL,
    FOREIGN KEY (fleet_id) REFERENCES fleets(id) ON DELETE SET NULL,
    FOREIGN KEY (vessel_id) REFERENCES vessels(id) ON DELETE SET NULL
);

CREATE INDEX IF NOT EXISTS idx_objective_refinement_sessions_tenant_objective_created
    ON objective_refinement_sessions(tenant_id, objective_id, created_utc DESC);
CREATE INDEX IF NOT EXISTS idx_objective_refinement_sessions_tenant_captain_status
    ON objective_refinement_sessions(tenant_id, captain_id, status);

CREATE TABLE IF NOT EXISTS objective_refinement_messages (
    id TEXT PRIMARY KEY,
    objective_refinement_session_id TEXT NOT NULL,
    objective_id TEXT NOT NULL,
    tenant_id TEXT,
    user_id TEXT,
    role TEXT NOT NULL,
    sequence INTEGER NOT NULL,
    content TEXT NOT NULL,
    is_selected BOOLEAN NOT NULL DEFAULT FALSE,
    created_utc TIMESTAMP NOT NULL,
    last_update_utc TIMESTAMP NOT NULL,
    FOREIGN KEY (objective_refinement_session_id) REFERENCES objective_refinement_sessions(id) ON DELETE CASCADE,
    FOREIGN KEY (objective_id) REFERENCES objectives(id) ON DELETE CASCADE,
    FOREIGN KEY (tenant_id) REFERENCES tenants(id) ON DELETE CASCADE,
    FOREIGN KEY (user_id) REFERENCES users(id) ON DELETE SET NULL
);

CREATE INDEX IF NOT EXISTS idx_objective_refinement_messages_session_sequence
    ON objective_refinement_messages(objective_refinement_session_id, sequence);
CREATE INDEX IF NOT EXISTS idx_objective_refinement_messages_objective_created
    ON objective_refinement_messages(objective_id, created_utc DESC);

INSERT INTO schema_migrations (version, description, applied_utc)
VALUES (42, 'Add normalized objectives backlog tables', NOW())
ON CONFLICT (version) DO NOTHING;
