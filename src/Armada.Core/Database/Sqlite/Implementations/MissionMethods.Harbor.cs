namespace Armada.Core.Database.Sqlite.Implementations
{
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Data.Sqlite;
    using Armada.Core.Enums;

    public partial class MissionMethods
    {
        /// <inheritdoc />
        public async Task<bool> TryBindHarborJobAsync(HarborMissionBindingUpdate update, CancellationToken token = default)
        {
            using (SqliteConnection connection = new SqliteConnection(_Driver.ConnectionString))
            {
                return await HarborMissionBindingPersistence.TryBindAsync(connection, DatabaseTypeEnum.Sqlite, update, token).ConfigureAwait(false);
            }
        }
    }
}
