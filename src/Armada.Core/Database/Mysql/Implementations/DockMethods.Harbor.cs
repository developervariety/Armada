namespace Armada.Core.Database.Mysql.Implementations
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using MySqlConnector;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    public partial class DockMethods
    {
        /// <inheritdoc />
        public async Task<bool> TryWriteHarborLifecycleIntentAsync(string dockId, string expectedBindingJson,
            HarborCheckoutLifecycleIntent? expected, HarborCheckoutLifecycleIntent next, CancellationToken token = default)
        {
            using (MySqlConnection connection = new MySqlConnection(_ConnectionString))
                return await HarborCheckoutLifecyclePersistence.TryWriteAsync(connection, DatabaseTypeEnum.Mysql,
                    dockId, expectedBindingJson, expected, next, token).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<Dock?> ReadByProvisionAttemptAsync(string provisionAttemptId, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(provisionAttemptId)) throw new ArgumentNullException(nameof(provisionAttemptId));
            using (MySqlConnection connection = new MySqlConnection(_ConnectionString))
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (MySqlCommand command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT * FROM docks WHERE provision_attempt_id = @attempt_id LIMIT 1;";
                    StoredValueBinder.Value(command, "@attempt_id", provisionAttemptId);
                    using (MySqlDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
                        if (await reader.ReadAsync(token).ConfigureAwait(false)) return DockColumns.Read(reader, MysqlDatabaseDriver.StoredValues);
                }
            }
            return null;
        }
    }
}
