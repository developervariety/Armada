namespace Armada.Core.Database.Postgresql.Implementations
{
    using System.Threading;
    using System.Threading.Tasks;
    using Npgsql;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    public partial class MissionMethods
    {
        /// <inheritdoc />
        public async Task<bool> TryRecordAdmissionAsync(Mission expected, MissionAdmissionObservation observation,
            CancellationToken token = default)
        {
            using (NpgsqlConnection connection = _Driver.CreateConnection())
            {
                return await MissionAdmissionPersistence.RecordAsync(connection, DatabaseTypeEnum.Postgresql,
                    expected, observation, token).ConfigureAwait(false);
            }
        }
    }
}
