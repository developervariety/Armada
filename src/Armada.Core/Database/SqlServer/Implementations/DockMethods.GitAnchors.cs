namespace Armada.Core.Database.SqlServer.Implementations
{
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Data.SqlClient;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    internal partial class DockMethods
    {
        /// <inheritdoc />
        public async Task<bool> TryCompleteGitAnchorsAsync(string dockId, string captainId,
            DockGitAnchorSnapshot expected, DockGitAnchorSnapshot completed, CancellationToken token = default)
        {
            using (SqlConnection connection = new SqlConnection(_Driver.ConnectionString))
            {
                return await DockGitAnchorUpdate.CompleteAsync(connection, DatabaseTypeEnum.SqlServer, dockId,
                    captainId, expected, completed, token).ConfigureAwait(false);
            }
        }
    }
}
