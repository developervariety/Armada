-- MySQL manual pre-stage for Armada v0.9.0 reliability release.
-- Mirrors automatic startup migration 44 (dock leases, process liveness,
-- review deadline, merge retry, coordination leases).
--
-- Note: older MySQL/MariaDB reject ADD COLUMN IF NOT EXISTS. If a column already
-- exists the statement errors; the automatic in-app migrator ignores error 1060
-- (duplicate column). When running by hand, skip any ADD COLUMN already present.

ALTER TABLE docks ADD COLUMN state VARCHAR(32) NOT NULL DEFAULT 'Available';
ALTER TABLE docks ADD COLUMN lease_expires_utc DATETIME(6) NULL;
ALTER TABLE docks ADD COLUMN owner_token TEXT NULL;
ALTER TABLE captains ADD COLUMN last_process_alive_utc DATETIME(6) NULL;
ALTER TABLE missions ADD COLUMN review_deadline_utc DATETIME(6) NULL;
ALTER TABLE merge_entries ADD COLUMN retry_count INT NOT NULL DEFAULT 0;
ALTER TABLE merge_entries ADD COLUMN lease_expires_utc DATETIME(6) NULL;

CREATE TABLE IF NOT EXISTS coordination_leases (
    name VARCHAR(255) NOT NULL PRIMARY KEY,
    holder TEXT NOT NULL,
    tenant_id VARCHAR(255) NULL,
    acquired_utc DATETIME(6) NOT NULL,
    expires_utc DATETIME(6) NOT NULL,
    INDEX idx_coordination_leases_expires (expires_utc)
);
