namespace Armada.Core.Database
{
    using System;
    using System.Data.Common;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    public sealed partial class AskThreadMethods
    {
        /// <inheritdoc />
        public async Task<bool> CompleteTurnAsync(string tenantId, string userId, string threadId, string requestId, CaptainChatResponse response, CancellationToken token = default)
        {
            ValidateOwner(tenantId, userId);
            if (response == null) throw new ArgumentNullException(nameof(response));
            string nextState = response.Success ? AskTurnStateEnum.Completed.ToString() : AskTurnStateEnum.Failed.ToString();
            string responseJson = JsonSerializer.Serialize(response);
            DateTime completedUtc = DateTime.UtcNow;
            using (DbConnection connection = _ConnectionFactory())
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (IDisposable? writeLease = await Sqlite.SqliteWriteGate.EnterAsync(connection, token).ConfigureAwait(false))
                    return await CompleteWithinTransactionAsync(connection, tenantId, userId, threadId, requestId, nextState, responseJson, completedUtc, token).ConfigureAwait(false);
            }
        }

    }
}
