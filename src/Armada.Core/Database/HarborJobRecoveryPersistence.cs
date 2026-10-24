namespace Armada.Core.Database
{
    /// <summary>Schema for durable Harbor attempt identity and recovery fences.</summary>
    internal static class HarborJobRecoveryPersistence
    {

        internal static readonly string[] SqliteStatements =
        {
            @"ALTER TABLE missions ADD COLUMN harbor_job_id TEXT NULL;",
            @"ALTER TABLE harbor_jobs ADD COLUMN dock_id TEXT NULL;",
            @"ALTER TABLE harbor_jobs ADD COLUMN admiral_process_id INTEGER NULL;",
            @"ALTER TABLE harbor_jobs ADD COLUMN admiral_process_started_utc TEXT NULL;",
            @"ALTER TABLE harbor_jobs ADD COLUMN runner_process_started_utc TEXT NULL;",
            @"ALTER TABLE harbor_jobs ADD COLUMN stop_confirmed INTEGER NOT NULL DEFAULT 0;",
            @"ALTER TABLE harbor_jobs ADD COLUMN completion_delivered INTEGER NOT NULL DEFAULT 0;",
            @"ALTER TABLE harbor_jobs ADD COLUMN stop_requested INTEGER NOT NULL DEFAULT 0;",
            @"UPDATE harbor_jobs SET stop_confirmed = 1 WHERE state = 'Exited';",
            @"UPDATE harbor_jobs SET stop_requested = 1 WHERE state = 'Stopping';",
            @"UPDATE harbor_jobs SET completion_delivered = 1 WHERE state IN ('Exited', 'Failed', 'Lost');",
            @"CREATE INDEX idx_harbor_jobs_mission ON harbor_jobs(mission_id);",
            @"CREATE INDEX idx_harbor_jobs_dock ON harbor_jobs(dock_id);",
            @"CREATE TABLE IF NOT EXISTS harbor_completion_intents (job_id TEXT NOT NULL PRIMARY KEY, payload TEXT NOT NULL, completed INTEGER NOT NULL DEFAULT 0);"
        };

        internal static readonly string[] PostgresqlStatements =
        {
            @"ALTER TABLE missions ADD COLUMN IF NOT EXISTS harbor_job_id TEXT NULL;",
            @"ALTER TABLE harbor_jobs ADD COLUMN IF NOT EXISTS dock_id TEXT NULL;",
            @"ALTER TABLE harbor_jobs ADD COLUMN IF NOT EXISTS admiral_process_id INTEGER NULL;",
            @"ALTER TABLE harbor_jobs ADD COLUMN IF NOT EXISTS admiral_process_started_utc TIMESTAMPTZ NULL;",
            @"ALTER TABLE harbor_jobs ADD COLUMN IF NOT EXISTS runner_process_started_utc TIMESTAMPTZ NULL;",
            @"ALTER TABLE harbor_jobs ADD COLUMN IF NOT EXISTS stop_confirmed INTEGER NOT NULL DEFAULT 0;",
            @"ALTER TABLE harbor_jobs ADD COLUMN IF NOT EXISTS completion_delivered INTEGER NOT NULL DEFAULT 0;",
            @"ALTER TABLE harbor_jobs ADD COLUMN IF NOT EXISTS stop_requested INTEGER NOT NULL DEFAULT 0;",
            @"UPDATE harbor_jobs SET stop_confirmed = 1 WHERE state = 'Exited';",
            @"UPDATE harbor_jobs SET stop_requested = 1 WHERE state = 'Stopping';",
            @"UPDATE harbor_jobs SET completion_delivered = 1 WHERE state IN ('Exited', 'Failed', 'Lost');",
            @"CREATE INDEX idx_harbor_jobs_mission ON harbor_jobs(mission_id);",
            @"CREATE INDEX idx_harbor_jobs_dock ON harbor_jobs(dock_id);",
            @"CREATE TABLE IF NOT EXISTS harbor_completion_intents (job_id TEXT NOT NULL PRIMARY KEY, payload TEXT NOT NULL, completed INTEGER NOT NULL DEFAULT 0);"
        };

        internal static readonly string[] MysqlStatements =
        {
            @"ALTER TABLE missions ADD COLUMN harbor_job_id VARCHAR(128) NULL;",
            @"ALTER TABLE harbor_jobs ADD COLUMN dock_id VARCHAR(128) NULL;",
            @"ALTER TABLE harbor_jobs ADD COLUMN admiral_process_id INT NULL;",
            @"ALTER TABLE harbor_jobs ADD COLUMN admiral_process_started_utc DATETIME(6) NULL;",
            @"ALTER TABLE harbor_jobs ADD COLUMN runner_process_started_utc DATETIME(6) NULL;",
            @"ALTER TABLE harbor_jobs ADD COLUMN stop_confirmed INT NOT NULL DEFAULT 0;",
            @"ALTER TABLE harbor_jobs ADD COLUMN completion_delivered INT NOT NULL DEFAULT 0;",
            @"ALTER TABLE harbor_jobs ADD COLUMN stop_requested INT NOT NULL DEFAULT 0;",
            @"UPDATE harbor_jobs SET stop_confirmed = 1 WHERE state = 'Exited';",
            @"UPDATE harbor_jobs SET stop_requested = 1 WHERE state = 'Stopping';",
            @"UPDATE harbor_jobs SET completion_delivered = 1 WHERE state IN ('Exited', 'Failed', 'Lost');",
            @"CREATE INDEX idx_harbor_jobs_mission ON harbor_jobs(mission_id);",
            @"CREATE INDEX idx_harbor_jobs_dock ON harbor_jobs(dock_id);",
            @"CREATE TABLE IF NOT EXISTS harbor_completion_intents (
    job_id VARCHAR(450) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL PRIMARY KEY,
    payload LONGTEXT NOT NULL,
    completed INT NOT NULL DEFAULT 0
) CHARACTER SET utf8mb4;"
        };

        internal static readonly string[] SqlServerStatements =
        {
            @"IF COL_LENGTH('missions', 'harbor_job_id') IS NULL ALTER TABLE missions ADD harbor_job_id NVARCHAR(128) NULL;",
            @"IF COL_LENGTH('harbor_jobs', 'dock_id') IS NULL ALTER TABLE harbor_jobs ADD dock_id NVARCHAR(128) NULL;",
            @"IF COL_LENGTH('harbor_jobs', 'admiral_process_id') IS NULL ALTER TABLE harbor_jobs ADD admiral_process_id INT NULL;",
            @"IF COL_LENGTH('harbor_jobs', 'admiral_process_started_utc') IS NULL ALTER TABLE harbor_jobs ADD admiral_process_started_utc NVARCHAR(64) NULL;",
            @"IF COL_LENGTH('harbor_jobs', 'runner_process_started_utc') IS NULL ALTER TABLE harbor_jobs ADD runner_process_started_utc NVARCHAR(64) NULL;",
            @"IF COL_LENGTH('harbor_jobs', 'stop_confirmed') IS NULL ALTER TABLE harbor_jobs ADD stop_confirmed INT NOT NULL DEFAULT 0;",
            @"IF COL_LENGTH('harbor_jobs', 'completion_delivered') IS NULL ALTER TABLE harbor_jobs ADD completion_delivered INT NOT NULL DEFAULT 0;",
            @"IF COL_LENGTH('harbor_jobs', 'stop_requested') IS NULL ALTER TABLE harbor_jobs ADD stop_requested INT NOT NULL DEFAULT 0;",
            @"UPDATE harbor_jobs SET stop_confirmed = 1 WHERE state = 'Exited';",
            @"UPDATE harbor_jobs SET stop_requested = 1 WHERE state = 'Stopping';",
            @"UPDATE harbor_jobs SET completion_delivered = 1 WHERE state IN ('Exited', 'Failed', 'Lost');",
            @"CREATE INDEX idx_harbor_jobs_mission ON harbor_jobs(mission_id);",
            @"CREATE INDEX idx_harbor_jobs_dock ON harbor_jobs(dock_id);",
            @"IF OBJECT_ID('harbor_completion_intents', 'U') IS NULL CREATE TABLE harbor_completion_intents (job_id NVARCHAR(450) NOT NULL PRIMARY KEY, payload NVARCHAR(MAX) NOT NULL, completed INT NOT NULL CONSTRAINT DF_harbor_completion_intents_completed DEFAULT 0);"
        };

    }
}
