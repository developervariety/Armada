namespace Armada.Core.Database
{
    using System;
    using System.Data.Common;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;

    /// <summary>
    /// Serializes schema inspection and migration on one database connection.
    /// </summary>
    internal sealed class SchemaInitializationLock : IAsyncDisposable
    {
        private readonly DbConnection _Connection;
        private readonly DatabaseTypeEnum _Provider;

        private SchemaInitializationLock(DbConnection connection, DatabaseTypeEnum provider)
        {
            _Connection = connection;
            _Provider = provider;
        }

        internal static async Task<SchemaInitializationLock> AcquireAsync(DbConnection connection,
            DatabaseTypeEnum provider, CancellationToken token)
        {
            using (DbCommand command = connection.CreateCommand())
            {
                command.CommandText = provider switch
                {
                    DatabaseTypeEnum.Postgresql => "SELECT pg_advisory_lock(617264616461::bigint); SELECT 1;",
                    DatabaseTypeEnum.SqlServer => "DECLARE @result int; EXEC @result = sys.sp_getapplock @Resource=N'Armada.Schema', @LockMode='Exclusive', @LockOwner='Session', @LockTimeout=30000; SELECT @result;",
                    DatabaseTypeEnum.Mysql => "SELECT GET_LOCK(SHA2(CONCAT('Armada.Schema.', DATABASE()), 256), 30);",
                    _ => throw new NotSupportedException("Server schema lock requires a server provider")
                };
                object? result = await command.ExecuteScalarAsync(token).ConfigureAwait(false);
                if (provider == DatabaseTypeEnum.SqlServer && (result == null || Convert.ToInt32(result) < 0)
                    || provider == DatabaseTypeEnum.Mysql && (result == null || Convert.ToInt32(result) != 1))
                    throw new InvalidOperationException("Could not obtain the database schema initialization lock");
            }
            return new SchemaInitializationLock(connection, provider);
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            using (DbCommand command = _Connection.CreateCommand())
            {
                command.CommandText = _Provider switch
                {
                    DatabaseTypeEnum.Postgresql => "SELECT pg_advisory_unlock(617264616461::bigint);",
                    DatabaseTypeEnum.SqlServer => "EXEC sys.sp_releaseapplock @Resource=N'Armada.Schema', @LockOwner='Session';",
                    DatabaseTypeEnum.Mysql => "SELECT RELEASE_LOCK(SHA2(CONCAT('Armada.Schema.', DATABASE()), 256));",
                    _ => throw new NotSupportedException()
                };
                await command.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
    }
}
