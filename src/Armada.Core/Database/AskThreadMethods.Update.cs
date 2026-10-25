namespace Armada.Core.Database
{
    using System;
    using System.Data.Common;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;

    public sealed partial class AskThreadMethods
    {
        /// <inheritdoc />
        public async Task<AskThread?> UpdateAsync(AskThread thread, CancellationToken token = default)
        {
            if (thread == null) throw new ArgumentNullException(nameof(thread));
            ValidateOwner(thread.TenantId, thread.UserId);
            using (DbConnection connection = _ConnectionFactory())
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (IDisposable? writeLease = await Sqlite.SqliteWriteGate.EnterAsync(connection, token).ConfigureAwait(false))
                using (DbCommand command = connection.CreateCommand())
                {
                    command.CommandText = "UPDATE ask_threads SET title = @title, captain_id = @captain_id, pinned = @pinned, archived = @archived WHERE id = @id AND tenant_id = @tenant_id AND user_id = @user_id;";
                    BindThread(command, thread);
                    if (await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1) return null;
                }
            }
            return await ReadAsync(thread.TenantId, thread.UserId, thread.Id, token).ConfigureAwait(false);
        }
    }
}
