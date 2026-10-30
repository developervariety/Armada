namespace Armada.Core.Database.Sqlite.Implementations
{
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Data.Sqlite;
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
            using (SqliteConnection connection = new SqliteConnection(_Driver.ConnectionString))
                return await MissionGeneratedArtifactReviewPersistence.TryWriteAsync(connection, DatabaseTypeEnum.Sqlite,
                    expected, next, prepareReview, token).ConfigureAwait(false);
        }
    }
}
