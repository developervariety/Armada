namespace Armada.Core.Database
{
    /// <summary>
    /// MySQL and SQL Server schema for planning sessions and their transcript messages. The columns, indexes and
    /// the per-session sequence uniqueness match the SQLite and PostgreSQL tables. A deleted tenant removes its
    /// sessions and a deleted session removes its messages; a deleted captain's sessions are removed by
    /// CascadeCleanup.
    /// </summary>
    internal static class PlanningSessionSchema
    {
        /// <summary>
        /// MySQL statements. Deleting a user clears the user reference, as on PostgreSQL.
        /// </summary>
        internal static readonly string[] MysqlStatements =
        {
            @"CREATE TABLE IF NOT EXISTS planning_sessions (
    id VARCHAR(450) NOT NULL PRIMARY KEY,
    tenant_id VARCHAR(450),
    user_id VARCHAR(450),
    captain_id VARCHAR(450) NOT NULL,
    vessel_id VARCHAR(450) NOT NULL,
    fleet_id VARCHAR(450),
    dock_id VARCHAR(450),
    branch_name LONGTEXT,
    title LONGTEXT NOT NULL,
    status VARCHAR(64) NOT NULL DEFAULT 'Created',
    pipeline_id VARCHAR(450),
    objective_id VARCHAR(450),
    selected_playbooks_json LONGTEXT,
    process_id INT NULL,
    failure_reason LONGTEXT,
    created_utc DATETIME(6) NOT NULL,
    started_utc DATETIME(6) NULL,
    completed_utc DATETIME(6) NULL,
    last_update_utc DATETIME(6) NOT NULL,
    FOREIGN KEY (tenant_id) REFERENCES tenants(id) ON DELETE CASCADE,
    FOREIGN KEY (user_id) REFERENCES users(id) ON DELETE SET NULL
);",
            @"CREATE TABLE IF NOT EXISTS planning_session_messages (
    id VARCHAR(450) NOT NULL PRIMARY KEY,
    planning_session_id VARCHAR(450) NOT NULL,
    tenant_id VARCHAR(450),
    user_id VARCHAR(450),
    role VARCHAR(64) NOT NULL,
    sequence INT NOT NULL,
    content LONGTEXT,
    is_selected_for_dispatch TINYINT(1) NOT NULL DEFAULT 0,
    created_utc DATETIME(6) NOT NULL,
    last_update_utc DATETIME(6) NOT NULL,
    FOREIGN KEY (planning_session_id) REFERENCES planning_sessions(id) ON DELETE CASCADE,
    FOREIGN KEY (tenant_id) REFERENCES tenants(id) ON DELETE CASCADE,
    FOREIGN KEY (user_id) REFERENCES users(id) ON DELETE SET NULL
);",
            @"CREATE INDEX idx_planning_sessions_tenant_user ON planning_sessions(tenant_id, user_id);",
            @"CREATE INDEX idx_planning_sessions_captain ON planning_sessions(captain_id);",
            @"CREATE INDEX idx_planning_sessions_status ON planning_sessions(status);",
            @"CREATE INDEX idx_planning_sessions_last_update ON planning_sessions(last_update_utc DESC);",
            @"CREATE INDEX idx_planning_session_messages_session ON planning_session_messages(planning_session_id);",
            @"CREATE UNIQUE INDEX idx_planning_session_messages_session_sequence ON planning_session_messages(planning_session_id, sequence);"
        };

        /// <summary>
        /// SQL Server statements. SQL Server refuses a second cascade path from one table to another, and users
        /// already cascade from tenants, so the user reference carries no foreign key and a message reaches its
        /// tenant only through its session.
        /// </summary>
        internal static readonly string[] SqlServerStatements =
        {
            @"IF OBJECT_ID('planning_sessions','U') IS NULL CREATE TABLE planning_sessions (
    id NVARCHAR(450) NOT NULL PRIMARY KEY,
    tenant_id NVARCHAR(450) NULL,
    user_id NVARCHAR(450) NULL,
    captain_id NVARCHAR(450) NOT NULL,
    vessel_id NVARCHAR(450) NOT NULL,
    fleet_id NVARCHAR(450) NULL,
    dock_id NVARCHAR(450) NULL,
    branch_name NVARCHAR(MAX) NULL,
    title NVARCHAR(MAX) NOT NULL,
    status NVARCHAR(64) NOT NULL DEFAULT 'Created',
    pipeline_id NVARCHAR(450) NULL,
    objective_id NVARCHAR(450) NULL,
    selected_playbooks_json NVARCHAR(MAX) NULL,
    process_id INT NULL,
    failure_reason NVARCHAR(MAX) NULL,
    created_utc DATETIME2 NOT NULL,
    started_utc DATETIME2 NULL,
    completed_utc DATETIME2 NULL,
    last_update_utc DATETIME2 NOT NULL,
    CONSTRAINT FK_planning_sessions_tenant FOREIGN KEY (tenant_id) REFERENCES tenants(id) ON DELETE CASCADE
);",
            @"IF OBJECT_ID('planning_session_messages','U') IS NULL CREATE TABLE planning_session_messages (
    id NVARCHAR(450) NOT NULL PRIMARY KEY,
    planning_session_id NVARCHAR(450) NOT NULL,
    tenant_id NVARCHAR(450) NULL,
    user_id NVARCHAR(450) NULL,
    role NVARCHAR(64) NOT NULL,
    sequence INT NOT NULL,
    content NVARCHAR(MAX) NULL,
    is_selected_for_dispatch BIT NOT NULL DEFAULT 0,
    created_utc DATETIME2 NOT NULL,
    last_update_utc DATETIME2 NOT NULL,
    CONSTRAINT FK_planning_session_messages_session FOREIGN KEY (planning_session_id) REFERENCES planning_sessions(id) ON DELETE CASCADE
);",
            @"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='idx_planning_sessions_tenant_user' AND object_id=OBJECT_ID('planning_sessions')) CREATE INDEX idx_planning_sessions_tenant_user ON planning_sessions(tenant_id, user_id);",
            @"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='idx_planning_sessions_captain' AND object_id=OBJECT_ID('planning_sessions')) CREATE INDEX idx_planning_sessions_captain ON planning_sessions(captain_id);",
            @"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='idx_planning_sessions_status' AND object_id=OBJECT_ID('planning_sessions')) CREATE INDEX idx_planning_sessions_status ON planning_sessions(status);",
            @"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='idx_planning_sessions_last_update' AND object_id=OBJECT_ID('planning_sessions')) CREATE INDEX idx_planning_sessions_last_update ON planning_sessions(last_update_utc DESC);",
            @"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='idx_planning_session_messages_session' AND object_id=OBJECT_ID('planning_session_messages')) CREATE INDEX idx_planning_session_messages_session ON planning_session_messages(planning_session_id);",
            @"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='idx_planning_session_messages_session_sequence' AND object_id=OBJECT_ID('planning_session_messages')) CREATE UNIQUE INDEX idx_planning_session_messages_session_sequence ON planning_session_messages(planning_session_id, sequence);"
        };
    }
}
