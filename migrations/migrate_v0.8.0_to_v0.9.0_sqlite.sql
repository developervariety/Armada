-- SQLite manual pre-stage for Armada v0.9.0 reliability release.
-- Mirrors automatic startup migration 44 (dock leases, process liveness,
-- review deadline, merge retry, coordination leases).
--
-- Note: SQLite ALTER TABLE ADD COLUMN has no IF NOT EXISTS. If a column already
-- exists, the statement errors; the automatic in-app migrator ignores the
-- "duplicate column name" error. When running this by hand, skip any ADD COLUMN
-- whose column is already present.

ALTER TABLE docks ADD COLUMN state TEXT NOT NULL DEFAULT 'Available';
ALTER TABLE docks ADD COLUMN lease_expires_utc TEXT;
ALTER TABLE docks ADD COLUMN owner_token TEXT;
ALTER TABLE captains ADD COLUMN last_process_alive_utc TEXT;
ALTER TABLE missions ADD COLUMN review_deadline_utc TEXT;
ALTER TABLE merge_entries ADD COLUMN retry_count INTEGER NOT NULL DEFAULT 0;
ALTER TABLE merge_entries ADD COLUMN lease_expires_utc TEXT;

CREATE TABLE IF NOT EXISTS coordination_leases (
    name TEXT PRIMARY KEY,
    holder TEXT NOT NULL,
    tenant_id TEXT,
    acquired_utc TEXT NOT NULL,
    expires_utc TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_coordination_leases_expires ON coordination_leases(expires_utc);
