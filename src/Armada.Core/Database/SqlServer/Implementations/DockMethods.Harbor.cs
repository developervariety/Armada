namespace Armada.Core.Database.SqlServer.Implementations
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Data.SqlClient;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    internal partial class DockMethods
    {
        /// <inheritdoc />
        public async Task<bool> TryWriteHarborLifecycleIntentAsync(string dockId, string expectedBindingJson,
            HarborCheckoutLifecycleIntent? expected, HarborCheckoutLifecycleIntent next, CancellationToken token = default)
        {
            using (SqlConnection connection = new SqlConnection(_Driver.ConnectionString))
                return await HarborCheckoutLifecyclePersistence.TryWriteAsync(connection, DatabaseTypeEnum.SqlServer,
                    dockId, expectedBindingJson, expected, next, token).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<Dock?> ReadByProvisionAttemptAsync(string provisionAttemptId, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(provisionAttemptId)) throw new ArgumentNullException(nameof(provisionAttemptId));
            using (SqlConnection connection = new SqlConnection(_Driver.ConnectionString))
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (SqlCommand command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT TOP 1 * FROM docks WHERE provision_attempt_id = @attempt_id;";
                    StoredValueBinder.Value(command, "@attempt_id", provisionAttemptId);
                    using (SqlDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
                        if (await reader.ReadAsync(token).ConfigureAwait(false)) return DockColumns.Read(reader, SqlServerDatabaseDriver.StoredValues);
                }
            }
            return null;
        }
    }
}
