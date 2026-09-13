namespace Armada.Core.Database.Sqlite.Implementations
{
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Data.Sqlite;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    public partial class DockMethods
    {
        /// <inheritdoc />
        public async Task<bool> TryCompleteGitAnchorsAsync(string dockId, string captainId,
            DockGitAnchorSnapshot expected, DockGitAnchorSnapshot completed, CancellationToken token = default)
        {
            using (SqliteConnection connection = new SqliteConnection(_Driver.ConnectionString))
            {
                return await DockGitAnchorUpdate.CompleteAsync(connection, DatabaseTypeEnum.Sqlite, dockId,
                    captainId, expected, completed, token).ConfigureAwait(false);
            }
        }
    }
}
