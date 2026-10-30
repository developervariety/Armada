namespace Armada.Core.Database.SqlServer.Implementations
{
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Data.SqlClient;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    internal partial class MissionMethods : IMissionMethods
    {
        /// <inheritdoc />
        public async Task<bool> TryWriteGeneratedArtifactReviewAsync(Mission expected, GeneratedArtifactReviewBinding next,
            bool prepareReview,
            CancellationToken token = default)
        {
            using (SqlConnection connection = new SqlConnection(_Driver.ConnectionString))
                return await MissionGeneratedArtifactReviewPersistence.TryWriteAsync(connection, DatabaseTypeEnum.SqlServer,
                    expected, next, prepareReview, token).ConfigureAwait(false);
        }
    }
}
