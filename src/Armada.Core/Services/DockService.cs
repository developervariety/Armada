namespace Armada.Core.Services
{
    using SyslogLogging;
    using Armada.Core.Database;
    using Armada.Core.Models;
    using Armada.Core.Settings;
    using Armada.Core.Services.Interfaces;

    /// <summary>
    /// Service for dock (worktree) lifecycle management.
    /// </summary>
    public class DockService : IDockService
    {
        #region Private-Members

        private string _Header = "[DockService] ";
        private LoggingModule _Logging;
        private DatabaseDriver _Database;
        private ArmadaSettings _Settings;
        private IGitService _Git;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="logging">Logging module.</param>
        /// <param name="database">Database driver.</param>
        /// <param name="settings">Application settings.</param>
        /// <param name="git">Git service.</param>
        public DockService(LoggingModule logging, DatabaseDriver database, ArmadaSettings settings, IGitService git)
        {
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _Git = git ?? throw new ArgumentNullException(nameof(git));
        }

        #endregion

        #region Public-Methods

        /// <inheritdoc />
        public async Task<Dock?> ProvisionAsync(Vessel vessel, Captain captain, string branchName, CancellationToken token = default)
        {
            if (vessel == null) throw new ArgumentNullException(nameof(vessel));
            if (captain == null) throw new ArgumentNullException(nameof(captain));
            if (String.IsNullOrEmpty(branchName)) throw new ArgumentNullException(nameof(branchName));

            string repoPath = vessel.LocalPath ?? Path.Combine(_Settings.ReposDirectory, vessel.Name + ".git");
            string worktreePath = Path.Combine(_Settings.DocksDirectory, vessel.Name, captain.Name);

            // Ensure bare clone exists
            if (!await _Git.IsRepositoryAsync(repoPath, token).ConfigureAwait(false))
            {
                if (String.IsNullOrEmpty(vessel.RepoUrl))
                    throw new InvalidOperationException("Vessel " + vessel.Name + " has no remote URL configured");
                await _Git.CloneBareAsync(vessel.RepoUrl, repoPath, token).ConfigureAwait(false);
                vessel.LocalPath = repoPath;
                await _Database.Vessels.UpdateAsync(vessel, token).ConfigureAwait(false);
            }

            // Prune stale worktree registrations (handles "missing but registered" entries)
            try
            {
                await _Git.PruneWorktreesAsync(repoPath, token).ConfigureAwait(false);
            }
            catch { }

            // Clean up ALL stale worktree directories under this vessel's dock directory.
            // This handles worktrees left behind by renamed/deleted captains that would
            // block git fetch with "refusing to fetch into branch checked out at..." errors.
            string vesselDockDir = Path.Combine(_Settings.DocksDirectory, vessel.Name);
            if (Directory.Exists(vesselDockDir))
            {
                foreach (string existingDir in Directory.GetDirectories(vesselDockDir))
                {
                    string dirName = Path.GetFileName(existingDir);

                    // Skip the current captain's directory — handled below
                    if (dirName == captain.Name) continue;

                    // Simple heuristic: if it's a git worktree but not for any active captain, clean it up
                    if (File.Exists(Path.Combine(existingDir, ".git")))
                    {
                        _Logging.Info(_Header + "cleaning up stale worktree from previous captain: " + existingDir);
                        try
                        {
                            await _Git.RemoveWorktreeAsync(existingDir, token).ConfigureAwait(false);
                        }
                        catch { }

                        if (Directory.Exists(existingDir))
                        {
                            try { Directory.Delete(existingDir, recursive: true); }
                            catch { }
                        }

                        // Re-prune after removing stale worktrees
                        try
                        {
                            await _Git.PruneWorktreesAsync(repoPath, token).ConfigureAwait(false);
                        }
                        catch { }
                    }
                }
            }

            // Clean up stale worktree directory if it exists from a previous run
            if (Directory.Exists(worktreePath))
            {
                _Logging.Info(_Header + "removing stale dock directory: " + worktreePath);
                try
                {
                    await _Git.RemoveWorktreeAsync(worktreePath, token).ConfigureAwait(false);
                }
                catch { }

                if (Directory.Exists(worktreePath))
                {
                    try { Directory.Delete(worktreePath, recursive: true); }
                    catch { }
                }
            }

            // Delete stale branch if it exists from a previous failed attempt
            try
            {
                await _Git.DeleteLocalBranchAsync(repoPath, branchName, token).ConfigureAwait(false);
            }
            catch { }

            // Create worktree
            try
            {
                await _Git.CreateWorktreeAsync(repoPath, worktreePath, branchName, vessel.DefaultBranch, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "failed to create worktree for branch " + branchName + ": " + ex.Message);
                return null;
            }

            // Create dock record
            Dock dock = new Dock(vessel.Id);
            dock.CaptainId = captain.Id;
            dock.WorktreePath = worktreePath;
            dock.BranchName = branchName;
            dock = await _Database.Docks.CreateAsync(dock, token).ConfigureAwait(false);

            _Logging.Info(_Header + "provisioned dock " + dock.Id + " at " + worktreePath);
            return dock;
        }

        /// <inheritdoc />
        public async Task ReclaimAsync(string dockId, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(dockId)) throw new ArgumentNullException(nameof(dockId));

            Dock? dock = await _Database.Docks.ReadAsync(dockId, token).ConfigureAwait(false);
            if (dock == null) return;

            if (!String.IsNullOrEmpty(dock.WorktreePath))
            {
                try
                {
                    await _Git.RemoveWorktreeAsync(dock.WorktreePath, token).ConfigureAwait(false);
                    _Logging.Info(_Header + "reclaimed dock " + dockId + " at " + dock.WorktreePath);
                }
                catch (Exception ex)
                {
                    _Logging.Warn(_Header + "error removing worktree for dock " + dockId + ": " + ex.Message);
                }
            }
        }

        /// <inheritdoc />
        public async Task RepairAsync(string dockId, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(dockId)) throw new ArgumentNullException(nameof(dockId));

            Dock? dock = await _Database.Docks.ReadAsync(dockId, token).ConfigureAwait(false);
            if (dock == null) throw new InvalidOperationException("Dock not found: " + dockId);

            if (!String.IsNullOrEmpty(dock.WorktreePath))
            {
                await _Git.RepairWorktreeAsync(dock.WorktreePath, token).ConfigureAwait(false);
                _Logging.Info(_Header + "repaired dock " + dockId);
            }
        }

        #endregion
    }
}
