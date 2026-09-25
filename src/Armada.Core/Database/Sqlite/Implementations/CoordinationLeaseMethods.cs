namespace Armada.Core.Database.Sqlite.Implementations
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Data.Sqlite;
    using SyslogLogging;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Models;
    using Armada.Core.Settings;

    /// <summary>
    /// SQLite implementation of durable coordination lease operations. Acquisition is an atomic
    /// compare-and-swap keyed by lease name using an upsert guarded on expiry, so a lease held by a
    /// live holder is never stolen while an expired lease may be taken over. Timestamps are stored
    /// as fixed-width ISO 8601 UTC text, which is lexicographically ordered and therefore safe to
    /// compare directly in SQL.
    /// </summary>
    public class CoordinationLeaseMethods : ICoordinationLeaseMethods
    {
        #region Private-Members

#pragma warning disable CS0414
        private readonly string _Header = "[CoordinationLeaseMethods] ";
#pragma warning restore CS0414
        private readonly SqliteDatabaseDriver _Driver;
        private readonly DatabaseSettings _Settings;
        private readonly LoggingModule _Logging;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="driver">SQLite database driver.</param>
        /// <param name="settings">Database settings.</param>
        /// <param name="logging">Logging module.</param>
        public CoordinationLeaseMethods(SqliteDatabaseDriver driver, DatabaseSettings settings, LoggingModule logging)
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
            if (String.IsNullOrEmpty(name)) throw new ArgumentNullException(nameof(name));
            if (String.IsNullOrEmpty(holder)) throw new ArgumentNullException(nameof(holder));

            DateTime now = DateTime.UtcNow;
            DateTime expires = now.Add(ttl);

            using (SqliteConnection conn = new SqliteConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqliteCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"INSERT INTO coordination_leases (name, holder, tenant_id, acquired_utc, expires_utc)
                            VALUES (@name, @holder, @tenant_id, @acquired_utc, @expires_utc)
                            ON CONFLICT(name) DO UPDATE SET
                                holder = excluded.holder,
                                tenant_id = excluded.tenant_id,
                                acquired_utc = excluded.acquired_utc,
                                expires_utc = excluded.expires_utc
                            WHERE coordination_leases.expires_utc <= @now;";
                    StoredValueBinder.Value(cmd, "@name", name);
                    StoredValueBinder.Value(cmd, "@holder", holder);
                    StoredValueBinder.Value(cmd, "@tenant_id", tenantId);
                    SqliteDatabaseDriver.StoredBinder.For(cmd, "coordination_leases").Utc("@acquired_utc", "acquired_utc", now);
                    SqliteDatabaseDriver.StoredBinder.For(cmd, "coordination_leases").Utc("@expires_utc", "expires_utc", expires);
                    SqliteDatabaseDriver.StoredBinder.For(cmd, "coordination_leases").Utc("@now", "expires_utc", now);
                    await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }

                using (SqliteCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT holder FROM coordination_leases WHERE name = @name;";
                    StoredValueBinder.Value(cmd, "@name", name);
                    object? result = await cmd.ExecuteScalarAsync(token).ConfigureAwait(false);
                    if (result == null || result == DBNull.Value) return false;
                    return String.Equals(result.ToString(), holder, StringComparison.Ordinal);
                }
            }
        }

        /// <inheritdoc />
        public async Task<bool> TryRenewAsync(string name, string holder, TimeSpan ttl, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(name)) throw new ArgumentNullException(nameof(name));
            if (String.IsNullOrEmpty(holder)) throw new ArgumentNullException(nameof(holder));

            DateTime now = DateTime.UtcNow;
            DateTime newExpires = now.Add(ttl);

            using (SqliteConnection conn = new SqliteConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqliteCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"UPDATE coordination_leases SET expires_utc = @new_expires
                            WHERE name = @name AND holder = @holder AND expires_utc > @now;";
                    SqliteDatabaseDriver.StoredBinder.For(cmd, "coordination_leases").Utc("@new_expires", "expires_utc", newExpires);
                    StoredValueBinder.Value(cmd, "@name", name);
                    StoredValueBinder.Value(cmd, "@holder", holder);
                    SqliteDatabaseDriver.StoredBinder.For(cmd, "coordination_leases").Utc("@now", "expires_utc", now);
                    int rowsAffected = await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                    return rowsAffected > 0;
                }
            }
        }

        /// <inheritdoc />
        public async Task ReleaseAsync(string name, string holder, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(name)) throw new ArgumentNullException(nameof(name));
            if (String.IsNullOrEmpty(holder)) throw new ArgumentNullException(nameof(holder));

            using (SqliteConnection conn = new SqliteConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqliteCommand cmd = conn.CreateCommand())
                {
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
            if (String.IsNullOrEmpty(name)) throw new ArgumentNullException(nameof(name));

            using (SqliteConnection conn = new SqliteConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqliteCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT * FROM coordination_leases WHERE name = @name;";
                    StoredValueBinder.Value(cmd, "@name", name);
                    using (SqliteDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        if (await reader.ReadAsync(token).ConfigureAwait(false))
                            return CoordinationLeaseColumns.Read(reader, SqliteDatabaseDriver.StoredValues);
                    }
                }
            }

            return null;
        }

        /// <inheritdoc />
        public async Task<int> PurgeExpiredAsync(CancellationToken token = default)
        {
            DateTime now = DateTime.UtcNow;

            using (SqliteConnection conn = new SqliteConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqliteCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "DELETE FROM coordination_leases WHERE expires_utc <= @now;";
                    SqliteDatabaseDriver.StoredBinder.For(cmd, "coordination_leases").Utc("@now", "expires_utc", now);
                    return await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }
        }

        #endregion
    }
}
