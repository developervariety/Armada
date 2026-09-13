namespace Armada.Core.Database.Mysql.Implementations
{
    using System.Threading;
    using System.Threading.Tasks;
    using MySqlConnector;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    public partial class DockMethods
    {
        /// <inheritdoc />
        public async Task<bool> TryCompleteGitAnchorsAsync(string dockId, string captainId,
            DockGitAnchorSnapshot expected, DockGitAnchorSnapshot completed, CancellationToken token = default)
        {
            using (MySqlConnection connection = new MySqlConnection(_ConnectionString))
            {
                return await DockGitAnchorUpdate.CompleteAsync(connection, DatabaseTypeEnum.Mysql, dockId,
                    captainId, expected, completed, token).ConfigureAwait(false);
            }
        }
    }
}
