namespace Armada.Core.Database
{
    using System;
    using System.Data.Common;
    using System.Threading;
    using System.Threading.Tasks;

    public sealed partial class AskThreadMethods
    {
        /// <inheritdoc />
        public Task<bool> MarkReadAsync(string tenantId, string userId, string threadId, CancellationToken token = default)
        {
            return SetUnreadAsync(tenantId, userId, threadId, 0, token);
        }

        /// <inheritdoc />
        public Task<bool> MarkUnreadAsync(string tenantId, string userId, string threadId, CancellationToken token = default)
        {
            return SetUnreadAsync(tenantId, userId, threadId, 1, token);
        }

        private async Task<bool> SetUnreadAsync(string tenantId, string userId, string threadId, int count, CancellationToken token)
        {
            ValidateOwner(tenantId, userId);
            if (String.IsNullOrWhiteSpace(threadId)) return false;
            using (DbConnection connection = _ConnectionFactory())
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (IDisposable? writeLease = await Sqlite.SqliteWriteGate.EnterAsync(connection, token).ConfigureAwait(false))
                using (DbCommand command = connection.CreateCommand())
                {
                    command.CommandText = "UPDATE ask_threads SET unread_count = @unread_count WHERE id = @id AND tenant_id = @tenant_id AND user_id = @user_id;";
                    Add(command, "@unread_count", count);
                    Add(command, "@id", threadId);
                    Add(command, "@tenant_id", tenantId);
                    Add(command, "@user_id", userId);
                    return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) == 1;
                }
            }
        }
    }
}
