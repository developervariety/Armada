-- PostgreSQL manual pre-stage for Armada v0.9.0 reliability release.
-- Mirrors automatic startup migration 44 (dock leases, process liveness,
-- review deadline, merge retry, coordination leases). Idempotent.

ALTER TABLE docks ADD COLUMN IF NOT EXISTS state TEXT NOT NULL DEFAULT 'Available';
ALTER TABLE docks ADD COLUMN IF NOT EXISTS lease_expires_utc TIMESTAMP;
ALTER TABLE docks ADD COLUMN IF NOT EXISTS owner_token TEXT;
ALTER TABLE captains ADD COLUMN IF NOT EXISTS last_process_alive_utc TIMESTAMP;
ALTER TABLE missions ADD COLUMN IF NOT EXISTS review_deadline_utc TIMESTAMP;
ALTER TABLE merge_entries ADD COLUMN IF NOT EXISTS retry_count INTEGER NOT NULL DEFAULT 0;
ALTER TABLE merge_entries ADD COLUMN IF NOT EXISTS lease_expires_utc TIMESTAMP;

CREATE TABLE IF NOT EXISTS coordination_leases (
    name TEXT PRIMARY KEY,
    holder TEXT NOT NULL,
    tenant_id TEXT,
    acquired_utc TIMESTAMP NOT NULL,
    expires_utc TIMESTAMP NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_coordination_leases_expires ON coordination_leases(expires_utc);
