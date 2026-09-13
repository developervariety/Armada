namespace Armada.Core.Database.Mysql.Implementations
{
    using System.Threading;
    using System.Threading.Tasks;
    using MySqlConnector;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    public partial class MissionMethods
    {
        /// <inheritdoc />
        public async Task<bool> TryRecordAdmissionAsync(Mission expected, MissionAdmissionObservation observation,
            CancellationToken token = default)
        {
            using (MySqlConnection connection = new MySqlConnection(_ConnectionString))
            {
                return await MissionAdmissionPersistence.RecordAsync(connection, DatabaseTypeEnum.Mysql,
                    expected, observation, token).ConfigureAwait(false);
            }
        }
    }
}
