namespace Armada.Server
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;

    /// <summary>
    /// The one reader for a mission's diff. REST, WebSocket and MCP call it, so every surface reads the saved diff file
    /// first, then the diff snapshot stored on the mission row, then the live worktree of the mission's dock.
    /// </summary>
    public static class MissionDiffReader
    {
        #region Public-Members

        /// <summary>
        /// Reason returned when no saved diff, snapshot or live worktree exists.
        /// </summary>
        public const string NotAvailableMessage = "No diff available — worktree was already reclaimed and no saved diff exists";

        /// <summary>
        /// Reason returned when only a live worktree diff could answer and no git service is configured.
        /// </summary>
        public const string GitUnavailableMessage = "No saved diff available and git service not configured";

        #endregion

        #region Public-Methods

        /// <summary>
        /// Read a mission's diff.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="logDirectory">Server log directory holding saved diffs.</param>
        /// <param name="git">Git service for a live worktree diff; null when none is configured.</param>
        /// <param name="mission">Mission, already read under the caller's scope. A summary read carries no snapshot, so the
        /// snapshot is read from the full row by id.</param>
        /// <param name="readDock">The surface's caller-scoped dock read; null reads without scope.</param>
        /// <param name="readCaptain">The surface's caller-scoped captain read; null reads without scope.</param>
        /// <param name="enumerateVesselDocks">The surface's caller-scoped read of a vessel's docks; null reads without scope.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The diff, or the reason there is none.</returns>
        public static async Task<MissionDiffResult> ReadAsync(
            DatabaseDriver database,
            string logDirectory,
            IGitService? git,
            Mission mission,
            Func<string, Task<Dock?>>? readDock = null,
            Func<string, Task<Captain?>>? readCaptain = null,
            Func<string, Task<List<Dock>>>? enumerateVesselDocks = null,
            CancellationToken token = default)
        {
            if (database == null) throw new ArgumentNullException(nameof(database));
            if (mission == null) throw new ArgumentNullException(nameof(mission));
            Func<string, Task<Dock?>> dockReader = readDock ?? (id => database.Docks.ReadAsync(id, token));
            Func<string, Task<Captain?>> captainReader = readCaptain ?? (id => database.Captains.ReadAsync(id, token));
            Func<string, Task<List<Dock>>> vesselDocks = enumerateVesselDocks ?? (id => database.Docks.EnumerateByVesselAsync(id, token));

            string savedDiffPath = Path.Combine(logDirectory, "diffs", mission.Id + ".diff");
            if (File.Exists(savedDiffPath))
            {
                string saved = await ReadTextSharedAsync(savedDiffPath, token).ConfigureAwait(false);
                return MissionDiffResult.Found(mission.Id, mission.BranchName ?? "", saved);
            }

            string? snapshot = mission.DiffSnapshot;
            if (String.IsNullOrEmpty(snapshot))
            {
                Mission? full = await database.Missions.ReadAsync(mission.Id, token).ConfigureAwait(false);
                snapshot = full?.DiffSnapshot;
            }
            if (!String.IsNullOrEmpty(snapshot))
                return MissionDiffResult.Found(mission.Id, mission.BranchName ?? "", snapshot);

            Dock? dock = null;
            if (!String.IsNullOrEmpty(mission.DockId))
                dock = await dockReader(mission.DockId).ConfigureAwait(false);
            if (dock == null && !String.IsNullOrEmpty(mission.CaptainId))
            {
                Captain? captain = await captainReader(mission.CaptainId).ConfigureAwait(false);
                if (captain != null && !String.IsNullOrEmpty(captain.CurrentDockId))
                    dock = await dockReader(captain.CurrentDockId).ConfigureAwait(false);
            }
            if (dock == null && !String.IsNullOrEmpty(mission.BranchName) && !String.IsNullOrEmpty(mission.VesselId))
            {
                List<Dock> docks = await vesselDocks(mission.VesselId).ConfigureAwait(false);
                dock = docks.FirstOrDefault(d => d.BranchName == mission.BranchName && d.Active);
            }

            if (dock == null || String.IsNullOrEmpty(dock.WorktreePath) || !Directory.Exists(dock.WorktreePath))
                return MissionDiffResult.NotAvailable(mission.Id, NotAvailableMessage);
            if (git == null)
                return MissionDiffResult.NotAvailable(mission.Id, GitUnavailableMessage);

            string baseBranch = "main";
            if (!String.IsNullOrEmpty(mission.VesselId))
            {
                Vessel? vessel = await database.Vessels.ReadAsync(mission.VesselId, token).ConfigureAwait(false);
                if (vessel != null) baseBranch = vessel.DefaultBranch;
            }

            string diff = await git.DiffAsync(dock.WorktreePath, baseBranch, token).ConfigureAwait(false);
            return MissionDiffResult.Found(mission.Id, dock.BranchName ?? "", diff);
        }

        #endregion

        #region Private-Methods

        private static async Task<string> ReadTextSharedAsync(string path, CancellationToken token)
        {
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (StreamReader reader = new StreamReader(stream))
            {
                return await reader.ReadToEndAsync(token).ConfigureAwait(false);
            }
        }

        #endregion
    }
}
