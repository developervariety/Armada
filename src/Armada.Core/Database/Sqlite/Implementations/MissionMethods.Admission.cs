namespace Armada.Core.Database.Sqlite.Implementations
{
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Data.Sqlite;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    public partial class MissionMethods
    {
        /// <inheritdoc />
        public async Task<bool> TryRecordAdmissionAsync(Mission expected, MissionAdmissionObservation observation,
            CancellationToken token = default)
        {
            using (SqliteConnection connection = new SqliteConnection(_Driver.ConnectionString))
            {
                return await MissionAdmissionPersistence.RecordAsync(connection, DatabaseTypeEnum.Sqlite,
                    expected, observation, token).ConfigureAwait(false);
            }
        }
    }
}
