namespace Test.Shared.Infrastructure
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Microsoft.Data.SqlClient;
    using MySqlConnector;
    using Npgsql;

    /// <summary>
    /// Creates and drops uniquely-named databases on a configured server (PostgreSQL, MySQL, or SQL Server)
    /// so each test case gets a fully isolated database that is torn down afterward. Only used for the
    /// non-SQLite providers; SQLite isolation is handled by <see cref="TestDatabaseHelper"/> with temp files.
    /// </summary>
    public static class TestDatabaseProvisioner
    {
        #region Public-Methods

        /// <summary>
        /// Create an empty database named <paramref name="databaseName"/> on the configured server, connecting
        /// through the provider's maintenance database.
        /// </summary>
        /// <param name="databaseName">Database name to create (a generated safe identifier).</param>
        /// <param name="token">Cancellation token.</param>
        /// <exception cref="NotSupportedException">Thrown when the configured provider is not a server provider.</exception>
        public static async Task CreateDatabaseAsync(string databaseName, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(databaseName)) throw new ArgumentNullException(nameof(databaseName));

            string maintenance = MaintenanceConnectionString();
            switch (TestDatabaseConfig.Type)
            {
                case DatabaseTypeEnum.Postgresql:
                    await ExecutePostgresAsync(maintenance, "CREATE DATABASE \"" + databaseName + "\";", token).ConfigureAwait(false);
                    break;
                case DatabaseTypeEnum.Mysql:
                    await ExecuteMysqlAsync(maintenance, "CREATE DATABASE `" + databaseName + "`;", token).ConfigureAwait(false);
                    break;
                case DatabaseTypeEnum.SqlServer:
                    await ExecuteSqlServerAsync(maintenance, "CREATE DATABASE [" + databaseName + "];", token).ConfigureAwait(false);
                    break;
                default:
                    throw new NotSupportedException("CreateDatabaseAsync is only supported for server providers, not " + TestDatabaseConfig.Type + ".");
            }
        }

        /// <summary>
        /// Drop the database named <paramref name="databaseName"/>, forcibly closing any lingering pooled
        /// connections first so the drop is not blocked. Best effort: no-op for SQLite.
        /// </summary>
        /// <param name="databaseName">Database name to drop.</param>
        /// <param name="token">Cancellation token.</param>
        public static async Task DropDatabaseAsync(string databaseName, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(databaseName)) throw new ArgumentNullException(nameof(databaseName));

            string maintenance = MaintenanceConnectionString();
            switch (TestDatabaseConfig.Type)
            {
                case DatabaseTypeEnum.Postgresql:
                    NpgsqlConnection.ClearAllPools();
                    await ExecutePostgresAsync(maintenance, "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '" + databaseName + "' AND pid <> pg_backend_pid();", token).ConfigureAwait(false);
                    await ExecutePostgresAsync(maintenance, "DROP DATABASE IF EXISTS \"" + databaseName + "\";", token).ConfigureAwait(false);
                    break;
                case DatabaseTypeEnum.Mysql:
                    MySqlConnection.ClearAllPools();
                    await ExecuteMysqlAsync(maintenance, "DROP DATABASE IF EXISTS `" + databaseName + "`;", token).ConfigureAwait(false);
                    break;
                case DatabaseTypeEnum.SqlServer:
                    SqlConnection.ClearAllPools();
                    await ExecuteSqlServerAsync(maintenance, "IF DB_ID('" + databaseName + "') IS NOT NULL BEGIN ALTER DATABASE [" + databaseName + "] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [" + databaseName + "]; END", token).ConfigureAwait(false);
                    break;
                default:
                    break;
            }
        }

        #endregion

        #region Private-Methods

        private static string MaintenanceConnectionString()
        {
            return TestDatabaseConfig.BuildSettings(TestDatabaseConfig.MaintenanceDatabaseName()).GetConnectionString();
        }

        private static async Task ExecutePostgresAsync(string connectionString, string sql, CancellationToken token)
        {
            using (NpgsqlConnection connection = new NpgsqlConnection(connectionString))
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (NpgsqlCommand command = connection.CreateCommand())
                {
                    command.CommandText = sql;
                    await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }
        }

        private static async Task ExecuteMysqlAsync(string connectionString, string sql, CancellationToken token)
        {
            using (MySqlConnection connection = new MySqlConnection(connectionString))
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (MySqlCommand command = connection.CreateCommand())
                {
                    command.CommandText = sql;
                    await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }
        }

        private static async Task ExecuteSqlServerAsync(string connectionString, string sql, CancellationToken token)
        {
            using (SqlConnection connection = new SqlConnection(connectionString))
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (SqlCommand command = connection.CreateCommand())
                {
                    command.CommandText = sql;
                    await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }
        }

        #endregion
    }
}
