namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;

    /// <summary>
    /// Deletes several docks by ID. The REST and MCP batch dock delete entry points both call this, so a
    /// batch delete obeys exactly the rule a single dock delete obeys: a dock that is active with a
    /// captain is refused, and the refusal is reported for that ID. Removing an active dock anyway is a
    /// separate, explicit force purge of that one dock.
    /// </summary>
    public static class DockBatchDelete
    {
        #region Public-Members

        /// <summary>
        /// Skip reason for a dock that was not found under the caller's scope.
        /// </summary>
        public const string NotFoundReason = "Not found";

        /// <summary>
        /// Skip reason for a dock that is active with a captain.
        /// </summary>
        public const string ActiveDockReason = "Dock is active with a captain; force purge it individually to remove it";

        #endregion

        #region Public-Methods

        /// <summary>
        /// Delete each dock through <see cref="IDockService.DeleteAsync"/> and report what was deleted and
        /// what was skipped, with the reason for each skipped ID.
        /// </summary>
        /// <param name="database">Database driver used to read each dock under the caller's scope.</param>
        /// <param name="docks">Dock service that performs the delete.</param>
        /// <param name="ids">Dock IDs to delete.</param>
        /// <param name="caller">Caller whose scope limits which docks are visible; null reads without scope.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Deleted count and the skipped IDs with reasons.</returns>
        public static async Task<DeleteMultipleResult> DeleteAsync(
            DatabaseDriver database,
            IDockService docks,
            IEnumerable<string> ids,
            AuthContext? caller,
            CancellationToken token = default)
        {
            if (database == null) throw new ArgumentNullException(nameof(database));
            if (docks == null) throw new ArgumentNullException(nameof(docks));
            if (ids == null) throw new ArgumentNullException(nameof(ids));

            DeleteMultipleResult result = new DeleteMultipleResult();
            foreach (string id in ids)
            {
                if (String.IsNullOrEmpty(id))
                {
                    result.Skipped.Add(new DeleteMultipleSkipped(id ?? "", "Empty ID"));
                    continue;
                }

                Dock? dock = await ReadScopedAsync(database, id, caller, token).ConfigureAwait(false);
                if (dock == null)
                {
                    result.Skipped.Add(new DeleteMultipleSkipped(id, NotFoundReason));
                    continue;
                }

                bool deleted = await docks.DeleteAsync(id, null, token).ConfigureAwait(false);
                if (deleted)
                    result.Deleted++;
                else
                    result.Skipped.Add(new DeleteMultipleSkipped(id, ActiveDockReason));
            }

            result.ResolveStatus();
            return result;
        }

        #endregion

        #region Private-Methods

        private static async Task<Dock?> ReadScopedAsync(DatabaseDriver database, string id, AuthContext? caller, CancellationToken token)
        {
            if (caller == null || caller.IsAdmin)
                return await database.Docks.ReadAsync(id, token).ConfigureAwait(false);
            if (String.IsNullOrEmpty(caller.TenantId))
                return null;
            if (caller.IsTenantAdmin)
                return await database.Docks.ReadAsync(caller.TenantId, id, token).ConfigureAwait(false);
            if (String.IsNullOrEmpty(caller.UserId))
                return null;
            return await database.Docks.ReadAsync(caller.TenantId, caller.UserId, id, token).ConfigureAwait(false);
        }

        #endregion
    }
}
