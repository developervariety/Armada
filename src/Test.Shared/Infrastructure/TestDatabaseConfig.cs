namespace Test.Shared.Infrastructure
{
    using System;
    using Armada.Core.Enums;
    using Armada.Core.Settings;

    /// <summary>
    /// Target database configuration for the test harness, read once from environment variables so the
    /// console runner, xUnit adapter, and NUnit adapter all honor the same selection. Defaults to SQLite so
    /// existing runs are unchanged. Set <c>ARMADA_TEST_DB_TYPE</c> to <c>postgresql</c>, <c>mysql</c>, or
    /// <c>sqlserver</c> (with the matching <c>ARMADA_TEST_DB_HOST</c> / <c>_PORT</c> / <c>_USER</c> /
    /// <c>_PASS</c> / <c>_NAME</c>) to run every DB-backed test against a real server. The Test.Automated CLI
    /// maps <c>--db-*</c> arguments onto these variables.
    /// </summary>
    public static class TestDatabaseConfig
    {
        #region Public-Members

        /// <summary>The configured database provider. SQLite when unset.</summary>
        public static DatabaseTypeEnum Type { get; }

        /// <summary>Whether the configured provider is SQLite (the default, template-file backed).</summary>
        public static bool IsSqlite
        {
            get { return Type == DatabaseTypeEnum.Sqlite; }
        }

        /// <summary>Server hostname for a non-SQLite provider (default 127.0.0.1).</summary>
        public static string Hostname { get; }

        /// <summary>Server port; 0 lets the provider default apply.</summary>
        public static int Port { get; }

        /// <summary>Server username.</summary>
        public static string Username { get; }

        /// <summary>Server password.</summary>
        public static string Password { get; }

        /// <summary>Base database name; each test gets a uniquely-suffixed database derived from it.</summary>
        public static string BaseDatabaseName { get; }

        #endregion

        #region Constructors-and-Factories

        static TestDatabaseConfig()
        {
            Type = ParseType(Environment.GetEnvironmentVariable("ARMADA_TEST_DB_TYPE"));
            Hostname = ReadEnv("ARMADA_TEST_DB_HOST", "127.0.0.1");
            Port = ParseInt(Environment.GetEnvironmentVariable("ARMADA_TEST_DB_PORT"), 0);
            Username = ReadEnv("ARMADA_TEST_DB_USER", String.Empty);
            Password = ReadEnv("ARMADA_TEST_DB_PASS", String.Empty);
            BaseDatabaseName = ReadEnv("ARMADA_TEST_DB_NAME", "armada_test");
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Build database settings targeting a specific database name on the configured server.
        /// </summary>
        /// <param name="databaseName">Database name to target. Required.</param>
        /// <returns>Settings for the configured provider pointing at <paramref name="databaseName"/>.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="databaseName"/> is empty.</exception>
        public static DatabaseSettings BuildSettings(string databaseName)
        {
            if (String.IsNullOrEmpty(databaseName)) throw new ArgumentNullException(nameof(databaseName));

            DatabaseSettings settings = new DatabaseSettings();
            settings.Type = Type;
            settings.Hostname = Hostname;
            settings.Port = Port;
            settings.Username = Username;
            settings.Password = Password;
            settings.DatabaseName = databaseName;
            return settings;
        }

        /// <summary>
        /// The always-present maintenance database used to run CREATE/DROP DATABASE for the configured
        /// provider (postgres, mysql, or master). Empty for SQLite, which needs no maintenance database.
        /// </summary>
        /// <returns>The maintenance database name.</returns>
        public static string MaintenanceDatabaseName()
        {
            switch (Type)
            {
                case DatabaseTypeEnum.Postgresql: return "postgres";
                case DatabaseTypeEnum.Mysql: return "mysql";
                case DatabaseTypeEnum.SqlServer: return "master";
                default: return String.Empty;
            }
        }

        #endregion

        #region Private-Methods

        private static string ReadEnv(string name, string fallback)
        {
            string? value = Environment.GetEnvironmentVariable(name);
            return String.IsNullOrEmpty(value) ? fallback : value;
        }

        private static int ParseInt(string? value, int fallback)
        {
            return Int32.TryParse(value, out int parsed) ? parsed : fallback;
        }

        private static DatabaseTypeEnum ParseType(string? value)
        {
            if (String.IsNullOrEmpty(value)) return DatabaseTypeEnum.Sqlite;
            switch (value.Trim().ToLowerInvariant())
            {
                case "postgres":
                case "postgresql": return DatabaseTypeEnum.Postgresql;
                case "mysql":
                case "mariadb": return DatabaseTypeEnum.Mysql;
                case "sqlserver":
                case "mssql": return DatabaseTypeEnum.SqlServer;
                default: return DatabaseTypeEnum.Sqlite;
            }
        }

        #endregion
    }
}
