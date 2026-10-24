namespace Armada.Core.Database.Mysql.Implementations
{
    using System.Threading;
    using System.Threading.Tasks;
    using MySqlConnector;
    using Armada.Core.Enums;

    public partial class MissionMethods
    {
        /// <inheritdoc />
        public async Task<bool> TryBindHarborJobAsync(HarborMissionBindingUpdate update, CancellationToken token = default)
        {
            using (MySqlConnection connection = new MySqlConnection(_ConnectionString))
            {
                return await HarborMissionBindingPersistence.TryBindAsync(connection, DatabaseTypeEnum.Mysql, update, token).ConfigureAwait(false);
            }
        }
    }
}
