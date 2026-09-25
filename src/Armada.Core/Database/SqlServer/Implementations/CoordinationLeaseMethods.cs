namespace Armada.Core.Database.SqlServer.Implementations
{
    using System;
    using System.Data;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Data.SqlClient;
    using SyslogLogging;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Models;
    using Armada.Core.Settings;

    /// <summary>
    /// SQL Server implementation of durable coordination-lease operations. Provides restart-safe,
    /// multi-instance-safe mutual exclusion via an atomic compare-and-swap on the lease name with
    /// TTL-based takeover, so a crashed holder never blocks progress permanently.
    /// </summary>
    internal class CoordinationLeaseMethods : ICoordinationLeaseMethods
    {
        #region Private-Members

#pragma warning disable CS0414
        private readonly string _Header = "[CoordinationLeaseMethods] ";
#pragma warning restore CS0414
        private readonly SqlServerDatabaseDriver _Driver;
        private readonly DatabaseSettings _Settings;
        private readonly LoggingModule _Logging;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="driver">SQL Server database driver.</param>
        /// <param name="settings">Database settings.</param>
        /// <param name="logging">Logging module.</param>
        internal CoordinationLeaseMethods(SqlServerDatabaseDriver driver, DatabaseSettings settings, LoggingModule logging)
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

            using (SqlConnection conn = new SqlConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);

                // Atomic compare-and-swap. HOLDLOCK on the MERGE takes a range/key lock (serializable
                // semantics) over the target name for the duration of the statement, so two concurrent
                // acquirers cannot both insert a new row or both take over the same expired lease --
                // one blocks until the other commits, then re-evaluates the ON/MATCHED predicates.
                using (SqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"
                        MERGE coordination_leases WITH (HOLDLOCK) AS target
                        USING (SELECT @name AS name) AS source
                        ON target.name = source.name
                        WHEN MATCHED AND target.expires_utc <= @now THEN
                            UPDATE SET holder = @holder, tenant_id = @tenant_id, acquired_utc = @now, expires_utc = @expires
                        WHEN NOT MATCHED THEN
                            INSERT (name, holder, tenant_id, acquired_utc, expires_utc)
                            VALUES (@name, @holder, @tenant_id, @now, @expires);";
                    StoredValueBinder.Value(cmd, "@name", name);
                    StoredValueBinder.Value(cmd, "@holder", holder);
                    StoredValueBinder.Value(cmd, "@tenant_id", tenantId);
                    SqlServerDatabaseDriver.StoredBinder.For(cmd, "coordination_leases").Utc("@now", "expires_utc", now);
                    SqlServerDatabaseDriver.StoredBinder.For(cmd, "coordination_leases").Utc("@expires", "expires_utc", expires);
                    await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }

                // Acquisition succeeded iff the current holder is us: either we inserted/took over the
                // row above, or the lease was already held by this same holder and left untouched.
                using (SqlCommand cmd = conn.CreateCommand())
                {
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
            DateTime expires = now.Add(ttl);

            using (SqlConnection conn = new SqlConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"UPDATE coordination_leases
                        SET expires_utc = @expires
                        WHERE name = @name AND holder = @holder AND expires_utc > @now;";
                    StoredValueBinder.Value(cmd, "@name", name);
                    StoredValueBinder.Value(cmd, "@holder", holder);
                    SqlServerDatabaseDriver.StoredBinder.For(cmd, "coordination_leases").Utc("@now", "expires_utc", now);
                    SqlServerDatabaseDriver.StoredBinder.For(cmd, "coordination_leases").Utc("@expires", "expires_utc", expires);
                    int affected = await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                    return affected > 0;
                }
            }
        }

        /// <inheritdoc />
        public async Task ReleaseAsync(string name, string holder, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(name)) throw new ArgumentNullException(nameof(name));
            if (string.IsNullOrEmpty(holder)) throw new ArgumentNullException(nameof(holder));

            using (SqlConnection conn = new SqlConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqlCommand cmd = conn.CreateCommand())
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
            if (string.IsNullOrEmpty(name)) throw new ArgumentNullException(nameof(name));

            using (SqlConnection conn = new SqlConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT * FROM coordination_leases WHERE name = @name;";
                    StoredValueBinder.Value(cmd, "@name", name);
                    using (SqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        if (await reader.ReadAsync(token).ConfigureAwait(false))
                            return CoordinationLeaseColumns.Read(reader, SqlServerDatabaseDriver.StoredValues);
                    }
                }
            }

            return null;
        }

        /// <inheritdoc />
        public async Task<int> PurgeExpiredAsync(CancellationToken token = default)
        {
            DateTime now = DateTime.UtcNow;

            using (SqlConnection conn = new SqlConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "DELETE FROM coordination_leases WHERE expires_utc <= @now;";
                    SqlServerDatabaseDriver.StoredBinder.For(cmd, "coordination_leases").Utc("@now", "expires_utc", now);
                    int affected = await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                    return affected;
                }
            }
        }

        #endregion
    }
}
