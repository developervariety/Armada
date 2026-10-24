namespace Armada.Core.Database.Postgresql.Implementations
{
    using System.Threading;
    using System.Threading.Tasks;
    using Npgsql;
    using Armada.Core.Enums;

    public partial class MissionMethods
    {
        /// <inheritdoc />
        public async Task<bool> TryBindHarborJobAsync(HarborMissionBindingUpdate update, CancellationToken token = default)
        {
            using (NpgsqlConnection connection = _Driver.CreateConnection())
            {
                return await HarborMissionBindingPersistence.TryBindAsync(connection, DatabaseTypeEnum.Postgresql, update, token).ConfigureAwait(false);
            }
        }
    }
}
