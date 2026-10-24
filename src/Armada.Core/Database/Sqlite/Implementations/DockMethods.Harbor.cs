namespace Armada.Core.Database.Sqlite.Implementations
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Data.Sqlite;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    public partial class DockMethods
    {
        /// <inheritdoc />
        public async Task<bool> TryWriteHarborLifecycleIntentAsync(string dockId, string expectedBindingJson,
            HarborCheckoutLifecycleIntent? expected, HarborCheckoutLifecycleIntent next, CancellationToken token = default)
        {
            using (SqliteConnection connection = new SqliteConnection(_Driver.ConnectionString))
                return await HarborCheckoutLifecyclePersistence.TryWriteAsync(connection, DatabaseTypeEnum.Sqlite,
                    dockId, expectedBindingJson, expected, next, token).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<Dock?> ReadByProvisionAttemptAsync(string provisionAttemptId, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(provisionAttemptId)) throw new ArgumentNullException(nameof(provisionAttemptId));
            using (SqliteConnection connection = new SqliteConnection(_Driver.ConnectionString))
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (SqliteCommand command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT * FROM docks WHERE provision_attempt_id = @attempt_id LIMIT 1;";
                    StoredValueBinder.Value(command, "@attempt_id", provisionAttemptId);
                    using (SqliteDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
                        if (await reader.ReadAsync(token).ConfigureAwait(false)) return DockColumns.Read(reader, SqliteDatabaseDriver.StoredValues);
                }
            }
            return null;
        }
    }
}
