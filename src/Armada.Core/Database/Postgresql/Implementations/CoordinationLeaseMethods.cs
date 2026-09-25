namespace Armada.Core.Database.Postgresql.Implementations
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Npgsql;
    using SyslogLogging;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Models;
    using Armada.Core.Settings;

    /// <summary>
    /// PostgreSQL implementation of durable coordination lease operations. Provides restart-safe,
    /// multi-instance-safe mutual exclusion via an atomic compare-and-swap on the lease name with
    /// TTL-based takeover, so a crashed holder never blocks progress permanently.
    /// </summary>
    public class CoordinationLeaseMethods : ICoordinationLeaseMethods
    {
        #region Private-Members

#pragma warning disable CS0414
        private string _Header = "[CoordinationLeaseMethods] ";
#pragma warning restore CS0414
        private PostgresqlDatabaseDriver _Driver;
        private DatabaseSettings _Settings;
        private LoggingModule _Logging;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="driver">PostgreSQL database driver.</param>
        /// <param name="settings">Database settings.</param>
        /// <param name="logging">Logging module.</param>
        public CoordinationLeaseMethods(PostgresqlDatabaseDriver driver, DatabaseSettings settings, LoggingModule logging)
        {
            _Driver = driver ?? throw new ArgumentNullException(nameof(driver));
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
        }

        #endregion

        #region Public-Methods

        /// <inheritdoc />
        public async Task<bool> TryAcquireAsync(string name, string holder, TimeSpan ttl, string? tenantId = null, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(name)) throw new ArgumentNullException(nameof(name));
            if (string.IsNullOrEmpty(holder)) throw new ArgumentNullException(nameof(holder));

            DateTime now = DateTime.UtcNow;
            DateTime expires = now.Add(ttl);

            using (NpgsqlConnection conn = new NpgsqlConnection(_Settings.GetConnectionString()))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (NpgsqlCommand cmd = new NpgsqlCommand())
                {
                    cmd.Connection = conn;
                    cmd.CommandText = @"INSERT INTO coordination_leases (name, holder, tenant_id, acquired_utc, expires_utc)
                        VALUES (@name, @holder, @tenant_id, @acquired_utc, @expires_utc)
                        ON CONFLICT (name) DO UPDATE SET
                            holder = EXCLUDED.holder,
                            tenant_id = EXCLUDED.tenant_id,
                            acquired_utc = EXCLUDED.acquired_utc,
                            expires_utc = EXCLUDED.expires_utc
                        WHERE coordination_leases.expires_utc <= @now;";
                    StoredValueBinder.Value(cmd, "@name", name);
                    StoredValueBinder.Value(cmd, "@holder", holder);
                    StoredValueBinder.Value(cmd, "@tenant_id", tenantId);
                    PostgresqlDatabaseDriver.StoredBinder.For(cmd, "coordination_leases").Utc("@acquired_utc", "acquired_utc", now);
                    PostgresqlDatabaseDriver.StoredBinder.For(cmd, "coordination_leases").Utc("@expires_utc", "expires_utc", expires);
                    PostgresqlDatabaseDriver.StoredBinder.For(cmd, "coordination_leases").Utc("@now", "expires_utc", now);
                    await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }

                using (NpgsqlCommand cmd = new NpgsqlCommand())
                {
                    cmd.Connection = conn;
                    cmd.CommandText = "SELECT holder FROM coordination_leases WHERE name = @name;";
                    StoredValueBinder.Value(cmd, "@name", name);
                    object? result = await cmd.ExecuteScalarAsync(token).ConfigureAwait(false);
                    if (result == null || result == DBNull.Value) return false;
                    return string.Equals(result.ToString(), holder, StringComparison.Ordinal);
                }
            }
        }

        /// <inheritdoc />
        public async Task<bool> TryRenewAsync(string name, string holder, TimeSpan ttl, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(name)) throw new ArgumentNullException(nameof(name));
            if (string.IsNullOrEmpty(holder)) throw new ArgumentNullException(nameof(holder));

            DateTime now = DateTime.UtcNow;
            DateTime newExpires = now.Add(ttl);

            using (NpgsqlConnection conn = new NpgsqlConnection(_Settings.GetConnectionString()))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (NpgsqlCommand cmd = new NpgsqlCommand())
                {
                    cmd.Connection = conn;
                    cmd.CommandText = @"UPDATE coordination_leases SET expires_utc = @new_expires
                        WHERE name = @name AND holder = @holder AND expires_utc > @now;";
                    PostgresqlDatabaseDriver.StoredBinder.For(cmd, "coordination_leases").Utc("@new_expires", "expires_utc", newExpires);
                    StoredValueBinder.Value(cmd, "@name", name);
                    StoredValueBinder.Value(cmd, "@holder", holder);
                    PostgresqlDatabaseDriver.StoredBinder.For(cmd, "coordination_leases").Utc("@now", "expires_utc", now);
                    int rowsAffected = await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                    return rowsAffected > 0;
                }
            }
        }

        /// <inheritdoc />
        public async Task ReleaseAsync(string name, string holder, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(name)) throw new ArgumentNullException(nameof(name));
            if (string.IsNullOrEmpty(holder)) throw new ArgumentNullException(nameof(holder));

            using (NpgsqlConnection conn = new NpgsqlConnection(_Settings.GetConnectionString()))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (NpgsqlCommand cmd = new NpgsqlCommand())
                {
                    cmd.Connection = conn;
                    cmd.CommandText = "DELETE FROM coordination_leases WHERE name = @name AND holder = @holder;";
                    StoredValueBinder.Value(cmd, "@name", name);
                    StoredValueBinder.Value(cmd, "@holder", holder);
                    await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }
        }

        /// <inheritdoc />
        public async Task<CoordinationLease?> ReadAsync(string name, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(name)) throw new ArgumentNullException(nameof(name));

            using (NpgsqlConnection conn = new NpgsqlConnection(_Settings.GetConnectionString()))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (NpgsqlCommand cmd = new NpgsqlCommand())
                {
                    cmd.Connection = conn;
                    cmd.CommandText = "SELECT * FROM coordination_leases WHERE name = @name;";
                    StoredValueBinder.Value(cmd, "@name", name);
                    using (NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        if (await reader.ReadAsync(token).ConfigureAwait(false))
                            return CoordinationLeaseColumns.Read(reader, PostgresqlDatabaseDriver.StoredValues);
                    }
                }
            }

            return null;
        }

        /// <inheritdoc />
        public async Task<int> PurgeExpiredAsync(CancellationToken token = default)
        {
            DateTime now = DateTime.UtcNow;

            using (NpgsqlConnection conn = new NpgsqlConnection(_Settings.GetConnectionString()))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (NpgsqlCommand cmd = new NpgsqlCommand())
                {
                    cmd.Connection = conn;
                    cmd.CommandText = "DELETE FROM coordination_leases WHERE expires_utc <= @now;";
                    PostgresqlDatabaseDriver.StoredBinder.For(cmd, "coordination_leases").Utc("@now", "expires_utc", now);
                    int rowsAffected = await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                    return rowsAffected;
                }
            }
        }

        #endregion
    }
}
