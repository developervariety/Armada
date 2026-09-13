namespace Armada.Core.Database
{
    using System;
    using System.Data.Common;
    using System.Globalization;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>Creates the first-boot identity in one transaction. Server callers hold the schema lock.</summary>
    internal static class DefaultDatabaseIdentity
    {
        internal static async Task EnsureAsync(DbConnection connection, DatabaseTypeEnum provider,
            Action<int, int>? checkpoint, CancellationToken token)
        {
            using (DbTransaction transaction = await connection.BeginTransactionAsync(token).ConfigureAwait(false))
            {
                if (!await ExistsAsync(connection, transaction, "tenants", Constants.DefaultTenantId, token).ConfigureAwait(false))
                {
                    using (DbCommand command = connection.CreateCommand())
                    {
                        command.Transaction = transaction;
                        command.CommandText = "INSERT INTO tenants(id,name,active,is_protected,created_utc,last_update_utc) VALUES (@id,@name,@active,@protected,@now,@now);";
                        Add(command, "@id", Constants.DefaultTenantId);
                        Add(command, "@name", Constants.DefaultTenantName);
                        AddCommon(command, provider);
                        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                    }
                }
                // Preserve existing installations, including deliberate credential removal.
                // Only a newly created default user receives a bootstrap credential.
                if (!await ExistsAsync(connection, transaction, "users", Constants.DefaultUserId, token).ConfigureAwait(false))
                {
                    using (DbCommand command = connection.CreateCommand())
                    {
                        command.Transaction = transaction;
                        command.CommandText = "INSERT INTO users(id,tenant_id,email,password_sha256,is_admin,is_tenant_admin,active,is_protected,created_utc,last_update_utc) VALUES (@id,@tenant,@email,@password,@active,@active,@active,@protected,@now,@now);";
                        Add(command, "@id", Constants.DefaultUserId);
                        Add(command, "@tenant", Constants.DefaultTenantId);
                        Add(command, "@email", Constants.DefaultUserEmail);
                        Add(command, "@password", UserMaster.ComputePasswordHash(Constants.DefaultUserPassword));
                        AddCommon(command, provider);
                        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                    }
                    checkpoint?.Invoke(52, -4);
                    using (DbCommand command = connection.CreateCommand())
                    {
                        command.Transaction = transaction;
                        command.CommandText = "INSERT INTO credentials(id,tenant_id,user_id,name,bearer_token,active,is_protected,created_utc,last_update_utc) VALUES (@id,@tenant,@user,@name,@bearer,@active,@protected,@now,@now);";
                        Add(command, "@id", Constants.DefaultCredentialId);
                        Add(command, "@tenant", Constants.DefaultTenantId);
                        Add(command, "@user", Constants.DefaultUserId);
                        Add(command, "@name", Constants.DefaultCredentialName);
                        Add(command, "@bearer", Constants.DefaultBearerToken);
                        AddCommon(command, provider);
                        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                    }
                }
                await transaction.CommitAsync(token).ConfigureAwait(false);
            }
        }

        private static async Task<bool> ExistsAsync(DbConnection connection, DbTransaction transaction, string table, string id, CancellationToken token)
        {
            using (DbCommand command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "SELECT COUNT(*) FROM " + table + " WHERE id=@id;";
                Add(command, "@id", id);
                return Convert.ToInt64(await command.ExecuteScalarAsync(token).ConfigureAwait(false), CultureInfo.InvariantCulture) > 0;
            }
        }

        private static void AddCommon(DbCommand command, DatabaseTypeEnum provider)
        {
            Add(command, "@active", true);
            Add(command, "@protected", true);
            DateTime now = DateTime.UtcNow;
            Add(command, "@now", provider == DatabaseTypeEnum.SqlServer || provider == DatabaseTypeEnum.Sqlite ? (object)now.ToString("o", CultureInfo.InvariantCulture) : now);
        }

        private static void Add(DbCommand command, string name, object value)
        {
            DbParameter parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }
    }
}
