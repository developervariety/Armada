namespace Armada.Core.Database.Postgresql.Implementations
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Npgsql;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    public partial class DockMethods
    {
        /// <inheritdoc />
        public async Task<bool> TryWriteHarborLifecycleIntentAsync(string dockId, string expectedBindingJson,
            HarborCheckoutLifecycleIntent? expected, HarborCheckoutLifecycleIntent next, CancellationToken token = default)
        {
            using (NpgsqlConnection connection = new NpgsqlConnection(_Driver.ConnectionString))
                return await HarborCheckoutLifecyclePersistence.TryWriteAsync(connection, DatabaseTypeEnum.Postgresql,
                    dockId, expectedBindingJson, expected, next, token).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<Dock?> ReadByProvisionAttemptAsync(string provisionAttemptId, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(provisionAttemptId)) throw new ArgumentNullException(nameof(provisionAttemptId));
            using (NpgsqlConnection connection = new NpgsqlConnection(_Driver.ConnectionString))
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (NpgsqlCommand command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT * FROM docks WHERE provision_attempt_id = @attempt_id LIMIT 1;";
                    StoredValueBinder.Value(command, "@attempt_id", provisionAttemptId);
                    using (NpgsqlDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
                        if (await reader.ReadAsync(token).ConfigureAwait(false)) return DockColumns.Read(reader, PostgresqlDatabaseDriver.StoredValues);
                }
            }
            return null;
        }
    }
}
