namespace Armada.Core.Database.Mysql.Implementations
{
    using System.Threading;
    using System.Threading.Tasks;
    using MySqlConnector;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    public partial class MissionMethods : IMissionMethods
    {
        /// <inheritdoc />
        public async Task<bool> TryWriteGeneratedArtifactReviewAsync(Mission expected, GeneratedArtifactReviewBinding next,
            bool prepareReview,
            CancellationToken token = default)
        {
            using (MySqlConnection connection = new MySqlConnection(_ConnectionString))
                return await MissionGeneratedArtifactReviewPersistence.TryWriteAsync(connection, DatabaseTypeEnum.Mysql,
                    expected, next, prepareReview, token).ConfigureAwait(false);
        }
    }
}
