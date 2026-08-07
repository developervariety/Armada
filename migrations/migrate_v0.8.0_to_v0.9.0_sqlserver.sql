-- SQL Server manual pre-stage for Armada v0.9.0 reliability release.
-- Mirrors automatic startup migration 44 (dock leases, process liveness,
-- review deadline, merge retry, coordination leases). Guarded for idempotency.

IF COL_LENGTH('docks', 'state') IS NULL
    ALTER TABLE docks ADD state NVARCHAR(32) NOT NULL CONSTRAINT DF_docks_state DEFAULT 'Available';
IF COL_LENGTH('docks', 'lease_expires_utc') IS NULL
    ALTER TABLE docks ADD lease_expires_utc DATETIME2 NULL;
IF COL_LENGTH('docks', 'owner_token') IS NULL
    ALTER TABLE docks ADD owner_token NVARCHAR(MAX) NULL;
IF COL_LENGTH('captains', 'last_process_alive_utc') IS NULL
    ALTER TABLE captains ADD last_process_alive_utc DATETIME2 NULL;
IF COL_LENGTH('missions', 'review_deadline_utc') IS NULL
    ALTER TABLE missions ADD review_deadline_utc DATETIME2 NULL;
IF COL_LENGTH('merge_entries', 'retry_count') IS NULL
    ALTER TABLE merge_entries ADD retry_count INT NOT NULL CONSTRAINT DF_merge_entries_retry_count DEFAULT 0;
IF COL_LENGTH('merge_entries', 'lease_expires_utc') IS NULL
    ALTER TABLE merge_entries ADD lease_expires_utc DATETIME2 NULL;

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'coordination_leases')
    CREATE TABLE coordination_leases (
        name NVARCHAR(255) NOT NULL PRIMARY KEY,
        holder NVARCHAR(MAX) NOT NULL,
        tenant_id NVARCHAR(255) NULL,
        acquired_utc DATETIME2 NOT NULL,
        expires_utc DATETIME2 NOT NULL
    );
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'idx_coordination_leases_expires')
    CREATE INDEX idx_coordination_leases_expires ON coordination_leases(expires_utc);
