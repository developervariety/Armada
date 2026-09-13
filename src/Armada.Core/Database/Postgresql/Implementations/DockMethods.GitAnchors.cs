namespace Armada.Core.Database.Postgresql.Implementations
{
    using System.Threading;
    using System.Threading.Tasks;
    using Npgsql;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    public partial class DockMethods
    {
        /// <inheritdoc />
        public async Task<bool> TryCompleteGitAnchorsAsync(string dockId, string captainId,
            DockGitAnchorSnapshot expected, DockGitAnchorSnapshot completed, CancellationToken token = default)
        {
            using (NpgsqlConnection connection = new NpgsqlConnection(_Settings.GetConnectionString()))
            {
                return await DockGitAnchorUpdate.CompleteAsync(connection, DatabaseTypeEnum.Postgresql, dockId,
                    captainId, expected, completed, token).ConfigureAwait(false);
            }
        }
    }
}
