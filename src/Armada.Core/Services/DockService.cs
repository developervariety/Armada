namespace Armada.Core.Services
{
    using SyslogLogging;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Settings;
    using Armada.Core.Services.Interfaces;

    /// <summary>
    /// Service for dock (worktree) lifecycle management.
    /// </summary>
    public class DockService : IDockService
    {
        #region Private-Members

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _RepoProvisionLocks =
            new System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.OrdinalIgnoreCase);

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
        public async Task<Dock?> ProvisionAsync(Vessel vessel, Captain captain, string branchName, string? missionId = null, CancellationToken token = default)
        {
            if (vessel == null) throw new ArgumentNullException(nameof(vessel));
            if (captain == null) throw new ArgumentNullException(nameof(captain));
            if (String.IsNullOrEmpty(branchName)) throw new ArgumentNullException(nameof(branchName));

            string repoPath = vessel.LocalPath ?? Path.Combine(_Settings.ReposDirectory, vessel.Name + ".git");

            // Use per-mission dock path when missionId is provided (eliminates path-reuse races).
            // Falls back to per-captain path for backward compatibility.
            string dockDirName = !String.IsNullOrEmpty(missionId) ? missionId : captain.Name;
            string worktreePath = Path.Combine(_Settings.DocksDirectory, vessel.Name, dockDirName);
            string normalizedRepoPath = Path.GetFullPath(repoPath);
            SemaphoreSlim repoLock = _RepoProvisionLocks.GetOrAdd(normalizedRepoPath, _ => new SemaphoreSlim(1, 1));
            bool repoLockAcquired = false;

            try
            {
                await repoLock.WaitAsync(token).ConfigureAwait(false);
                repoLockAcquired = true;

                // Ensure bare clone exists. If the directory exists but isn't a valid repo
                // (e.g. leftover from a failed clone/seed), remove it and re-clone.
                if (Directory.Exists(repoPath) && !await _Git.IsRepositoryAsync(repoPath, token).ConfigureAwait(false))
                {
                    _Logging.Warn(_Header + "removing corrupt/incomplete repo directory: " + repoPath);
                    await ForceRemoveDirectoryAsync(repoPath, token).ConfigureAwait(false);
                }

                if (!await _Git.IsRepositoryAsync(repoPath, token).ConfigureAwait(false))
                {
                    if (String.IsNullOrEmpty(vessel.RepoUrl))
                        throw new InvalidOperationException("Vessel " + vessel.Name + " has no remote URL configured");
                    await _Git.CloneBareAsync(vessel.RepoUrl, repoPath, token).ConfigureAwait(false);
                    vessel.LocalPath = repoPath;
                    await _Database.Vessels.UpdateAsync(vessel, token).ConfigureAwait(false);
                }

                // Ensure the configured default branch exists locally. Missing configured branches
                // in non-empty repos should be created from the remote/default history rather than
                // being treated as an empty repository.
                bool hasDefaultBranch = await _Git.EnsureLocalBranchAsync(repoPath, vessel.DefaultBranch, token).ConfigureAwait(false);
                if (!hasDefaultBranch)
                {
                    _Logging.Info(_Header + "bare repo for " + vessel.Name + " has no usable branch history for " + vessel.DefaultBranch + " -- seeding initial commit");
                    await SeedEmptyRepoAsync(vessel, repoPath, token).ConfigureAwait(false);
                }
                else if (String.IsNullOrEmpty(vessel.LocalPath))
                {
                    _Logging.Info(_Header + "bare repo exists but vessel LocalPath is empty for " + vessel.Name + ", updating to " + repoPath);
                    vessel.LocalPath = repoPath;
                    await _Database.Vessels.UpdateAsync(vessel, token).ConfigureAwait(false);
                }

                // Fetch latest from remote to ensure worktrees branch from current main
                if (!String.IsNullOrEmpty(vessel.RepoUrl))
                {
                    try
                    {
                        await _Git.FetchAsync(repoPath, token).ConfigureAwait(false);
                    }
                    catch (Exception fetchEx)
                    {
                        _Logging.Warn(_Header + "fetch failed for " + vessel.Name + ", continuing with local state: " + fetchEx.Message);
                    }
                }

                // Prune stale worktree registrations (handles "missing but registered" entries)
                try
                {
                    await _Git.PruneWorktreesAsync(repoPath, token).ConfigureAwait(false);
                }
                catch { }

                // Clean up stale worktree directories under this vessel's dock directory.
                // Only removes directories that are NOT associated with any active dock record.
                string vesselDockDir = Path.Combine(_Settings.DocksDirectory, vessel.Name);
                if (Directory.Exists(vesselDockDir))
                {
                    // Query active docks for this vessel to avoid deleting in-use worktrees
                    List<Dock> vesselDocks = await _Database.Docks.EnumerateByVesselAsync(vessel.Id, token).ConfigureAwait(false);
                    HashSet<string> activeDockPaths = new HashSet<string>(
                        vesselDocks
                            .Where(d => d.Active && !String.IsNullOrEmpty(d.WorktreePath))
                            .Select(d => d.WorktreePath!),
                        StringComparer.OrdinalIgnoreCase);

                    foreach (string existingDir in Directory.GetDirectories(vesselDockDir))
                    {
                        string dirName = Path.GetFileName(existingDir);

                        // Skip the current captain's directory -- handled below
                        if (dirName == captain.Name) continue;

                        // Skip directories belonging to active docks
                        if (activeDockPaths.Contains(existingDir))
                        {
                            _Logging.Info(_Header + "skipping cleanup of " + existingDir + ": still in use by an active dock");
                            continue;
                        }

                        // Only clean up directories that look like git worktrees or repos
                        // and are not associated with any active dock.
                        string dotGitPath = Path.Combine(existingDir, ".git");
                        if (File.Exists(dotGitPath) || Directory.Exists(dotGitPath))
                        {
                            // Only attempt git worktree remove if the path is actually registered
                            bool isRegistered = await _Git.IsWorktreeRegisteredAsync(repoPath, existingDir, token).ConfigureAwait(false);
                            if (isRegistered)
                            {
                                _Logging.Info(_Header + "cleaning up stale worktree from previous captain: " + existingDir);
                                try
                                {
                                    await _Git.RemoveWorktreeAsync(existingDir, token).ConfigureAwait(false);
                                }
                                catch { }
                            }
                            else
                            {
                                _Logging.Debug(_Header + "removing unregistered worktree directory: " + existingDir);
                            }

                            if (Directory.Exists(existingDir))
                            {
                                await ForceRemoveDirectoryAsync(existingDir, token).ConfigureAwait(false);
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
                    bool isRegistered = await _Git.IsWorktreeRegisteredAsync(repoPath, worktreePath, token).ConfigureAwait(false);
                    if (isRegistered)
                    {
                        _Logging.Info(_Header + "removing stale dock directory: " + worktreePath);
                        try
                        {
                            await _Git.RemoveWorktreeAsync(worktreePath, token).ConfigureAwait(false);
                        }
                        catch (Exception rmEx)
                        {
                            _Logging.Warn(_Header + "git worktree remove failed for " + worktreePath + ": " + rmEx.Message);
                        }
                    }
                    else
                    {
                        _Logging.Debug(_Header + "removing unregistered dock directory: " + worktreePath);
                    }

                    await ForceRemoveDirectoryAsync(worktreePath, token).ConfigureAwait(false);
                }

                // Create worktree. If the branch already exists, GitService will attach to it
                // instead of recreating it so downstream pipeline stages and retries preserve work.
                await _Git.CreateWorktreeAsync(repoPath, worktreePath, branchName, vessel.DefaultBranch, token).ConfigureAwait(false);
                await SeedDockMcpConfigAsync(vessel, worktreePath, missionId, token).ConfigureAwait(false);

                // Provision declared sibling repositories alongside this dock so consumer repos
                // that resolve cross-repo sources via parent-probe paths can build inside the dock.
                // No-op for vessels that declare no siblings (single-repo vessels are unaffected).
                await ProvisionSiblingReposAsync(vessel, worktreePath, branchName, token).ConfigureAwait(false);

                string? headCommit = await _Git.GetHeadCommitHashAsync(worktreePath, token).ConfigureAwait(false);
                if (String.IsNullOrEmpty(headCommit))
                {
                    throw new InvalidOperationException("Provisioned worktree " + worktreePath +
                        " for branch " + branchName + " without a valid HEAD commit");
                }

                // Create dock record
                Dock dock = new Dock(vessel.Id);
                dock.TenantId = vessel.TenantId;
                dock.UserId = vessel.UserId;
                dock.CaptainId = captain.Id;
                dock.WorktreePath = worktreePath;
                dock.BranchName = branchName;
                dock = await _Database.Docks.CreateAsync(dock, token).ConfigureAwait(false);

                try
                {
                    await PersistDockStartCommitAsync(dock.Id, headCommit, token).ConfigureAwait(false);
                }
                catch (Exception metadataEx)
                {
                    _Logging.Warn(_Header + "unable to persist dock start commit for " + dock.Id + ": " + metadataEx.Message);
                }

                _Logging.Info(_Header + "provisioned dock " + dock.Id + " at " + worktreePath);
                return dock;
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "provisioning failed for vessel " + vessel.Id + " captain " + captain.Id + " repo " + (vessel.RepoUrl ?? "unknown") + ": " + ex.Message);

                // Clean up partial state -- remove worktree directory if it was partially created
                if (Directory.Exists(worktreePath))
                {
                    await ForceRemoveDirectoryAsync(worktreePath, CancellationToken.None).ConfigureAwait(false);
                }

                return null;
            }
            finally
            {
                if (repoLockAcquired)
                    repoLock.Release();
            }
        }

        /// <inheritdoc />
        public async Task ReclaimAsync(string dockId, string? tenantId = null, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(dockId)) throw new ArgumentNullException(nameof(dockId));

            Dock? dock = !String.IsNullOrEmpty(tenantId)
                ? await _Database.Docks.ReadAsync(tenantId, dockId, token).ConfigureAwait(false)
                : await _Database.Docks.ReadAsync(dockId, token).ConfigureAwait(false);
            if (dock == null) return;

            // Idempotency guard: if the dock is already inactive, it was already reclaimed.
            // This prevents duplicate worktree removal when both MissionService (background finalizer)
            // and ArmadaServer (HandleMissionCompleteAsync) both call ReclaimAsync for the same dock.
            if (!dock.Active)
            {
                _Logging.Debug(_Header + "dock " + dockId + " already reclaimed (Active=false) -- skipping");
                return;
            }

            if (!String.IsNullOrEmpty(dock.WorktreePath))
            {
                if (await IsWorktreeOwnedByAnotherActiveDockAsync(dock, token).ConfigureAwait(false))
                {
                    _Logging.Warn(_Header + "skipping worktree removal for dock " + dockId +
                        " because another active dock now owns path " + dock.WorktreePath);
                }
                else
                {
                    try
                    {
                        Vessel? vessel = await _Database.Vessels.ReadAsync(dock.VesselId, token).ConfigureAwait(false);
                        bool isRegistered = !String.IsNullOrEmpty(vessel?.LocalPath) &&
                            await _Git.IsWorktreeRegisteredAsync(vessel.LocalPath, dock.WorktreePath, token).ConfigureAwait(false);

                        if (isRegistered)
                        {
                            await _Git.RemoveWorktreeAsync(dock.WorktreePath, token).ConfigureAwait(false);
                            _Logging.Info(_Header + "reclaimed dock " + dockId + " at " + dock.WorktreePath);
                        }
                        else
                        {
                            _Logging.Debug(_Header + "dock " + dockId + " worktree " + dock.WorktreePath +
                                " is not registered -- removing directory directly");
                        }
                    }
                    catch (Exception ex)
                    {
                        _Logging.Warn(_Header + "error removing worktree for dock " + dockId + ": " + ex.Message);
                    }

                    // Ensure the directory is actually removed -- on Windows, file handles
                    // from the just-exited agent process can linger and block deletion.
                    await ForceRemoveDirectoryAsync(dock.WorktreePath, token).ConfigureAwait(false);
                }
            }

            await RemoveSiblingReposForDockAsync(dock, token).ConfigureAwait(false);

            TryDeleteDockStartCommitFile(dock.Id);

            // Update the dock record so DataExpiryService can purge it
            dock.Active = false;
            dock.CaptainId = null;
            dock.LastUpdateUtc = DateTime.UtcNow;
            await _Database.Docks.UpdateAsync(dock, token).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task RepairAsync(string dockId, string? tenantId = null, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(dockId)) throw new ArgumentNullException(nameof(dockId));

            Dock? dock = !String.IsNullOrEmpty(tenantId)
                ? await _Database.Docks.ReadAsync(tenantId, dockId, token).ConfigureAwait(false)
                : await _Database.Docks.ReadAsync(dockId, token).ConfigureAwait(false);
            if (dock == null) throw new InvalidOperationException("Dock not found: " + dockId);

            if (!String.IsNullOrEmpty(dock.WorktreePath))
            {
                await _Git.RepairWorktreeAsync(dock.WorktreePath, token).ConfigureAwait(false);
                _Logging.Info(_Header + "repaired dock " + dockId);
            }
        }

        /// <inheritdoc />
        public async Task<bool> DeleteAsync(string dockId, string? tenantId = null, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(dockId)) throw new ArgumentNullException(nameof(dockId));

            Dock? dock = !String.IsNullOrEmpty(tenantId)
                ? await _Database.Docks.ReadAsync(tenantId, dockId, token).ConfigureAwait(false)
                : await _Database.Docks.ReadAsync(dockId, token).ConfigureAwait(false);
            if (dock == null) throw new InvalidOperationException("Dock not found: " + dockId);

            // Block deletion if an active mission is using this dock
            if (dock.Active && !String.IsNullOrEmpty(dock.CaptainId))
            {
                _Logging.Warn(_Header + "cannot delete dock " + dockId + " -- it is active with captain " + dock.CaptainId);
                return false;
            }

            await CleanupWorktreeAsync(dock, token).ConfigureAwait(false);

            // Delete the captain branch per vessel policy before removing the dock record.
            // Branch may already be gone if the merge-queue land ran first -- git failures are swallowed.
            // See also: MissionLandingHandler.CleanupMissionBranchAsync which applies the same policy for the landing path.
            await CleanupDockBranchAsync(dock, token).ConfigureAwait(false);

            if (!String.IsNullOrEmpty(tenantId))
                await _Database.Docks.DeleteAsync(tenantId, dockId, token).ConfigureAwait(false);
            else
                await _Database.Docks.DeleteAsync(dockId, token).ConfigureAwait(false);

            _Logging.Info(_Header + "deleted dock " + dockId);
            return true;
        }

        /// <inheritdoc />
        public async Task PurgeAsync(string dockId, string? tenantId = null, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(dockId)) throw new ArgumentNullException(nameof(dockId));

            Dock? dock = !String.IsNullOrEmpty(tenantId)
                ? await _Database.Docks.ReadAsync(tenantId, dockId, token).ConfigureAwait(false)
                : await _Database.Docks.ReadAsync(dockId, token).ConfigureAwait(false);
            if (dock == null) throw new InvalidOperationException("Dock not found: " + dockId);

            await CleanupWorktreeAsync(dock, token).ConfigureAwait(false);

            if (!String.IsNullOrEmpty(tenantId))
                await _Database.Docks.DeleteAsync(tenantId, dockId, token).ConfigureAwait(false);
            else
                await _Database.Docks.DeleteAsync(dockId, token).ConfigureAwait(false);

            _Logging.Info(_Header + "purged dock " + dockId + " (force)");
        }

        #endregion

        #region Private-Methods

        /// <summary>
        /// Clean up a dock's worktree by removing the git worktree and directory.
        /// </summary>
        private async Task CleanupWorktreeAsync(Dock dock, CancellationToken token)
        {
            if (!String.IsNullOrEmpty(dock.WorktreePath))
            {
                try
                {
                    await _Git.RemoveWorktreeAsync(dock.WorktreePath, token).ConfigureAwait(false);
                    _Logging.Info(_Header + "removed worktree for dock " + dock.Id + " at " + dock.WorktreePath);
                }
                catch (Exception ex)
                {
                    _Logging.Warn(_Header + "error removing worktree for dock " + dock.Id + ": " + ex.Message);
                }

                await ForceRemoveDirectoryAsync(dock.WorktreePath, token).ConfigureAwait(false);
            }

            await RemoveSiblingReposForDockAsync(dock, token).ConfigureAwait(false);

            TryDeleteDockStartCommitFile(dock.Id);
        }

        /// <summary>
        /// Delete the dock's captain branch per the parent vessel's BranchCleanupPolicy.
        /// Git failures are swallowed -- the branch may already be gone if the merge-queue
        /// landing path ran first.
        /// </summary>
        private async Task CleanupDockBranchAsync(Dock dock, CancellationToken token)
        {
            if (String.IsNullOrEmpty(dock.BranchName)) return;

            Vessel? vessel = null;
            try
            {
                vessel = !String.IsNullOrEmpty(dock.TenantId)
                    ? await _Database.Vessels.ReadAsync(dock.TenantId, dock.VesselId, token).ConfigureAwait(false)
                    : await _Database.Vessels.ReadAsync(dock.VesselId, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "could not read vessel " + dock.VesselId + " for branch cleanup: " + ex.Message);
            }

            BranchCleanupPolicyEnum cleanupPolicy = vessel?.BranchCleanupPolicy ?? _Settings.BranchCleanupPolicy;

            if (cleanupPolicy == BranchCleanupPolicyEnum.None)
            {
                _Logging.Info(_Header + "branch cleanup policy is None - retaining branch " + dock.BranchName + " for dock " + dock.Id);
                return;
            }

            string repoPath = vessel?.LocalPath ?? (vessel != null ? Path.Combine(_Settings.ReposDirectory, vessel.Name + ".git") : "");
            if (String.IsNullOrEmpty(repoPath)) return;

            try
            {
                await _Git.DeleteLocalBranchAsync(repoPath, dock.BranchName, token).ConfigureAwait(false);
                _Logging.Info(_Header + "deleted branch " + dock.BranchName + " from bare repo for dock " + dock.Id);
            }
            catch (Exception ex)
            {
                _Logging.Debug(_Header + "could not delete branch " + dock.BranchName + " from bare repo for dock " + dock.Id + ": " + ex.Message);
            }

            if (cleanupPolicy == BranchCleanupPolicyEnum.LocalAndRemote && !String.IsNullOrEmpty(vessel?.WorkingDirectory))
            {
                try
                {
                    await _Git.DeleteRemoteBranchAsync(vessel.WorkingDirectory, dock.BranchName, token).ConfigureAwait(false);
                    _Logging.Info(_Header + "deleted remote branch " + dock.BranchName + " for dock " + dock.Id);
                }
                catch (Exception ex)
                {
                    _Logging.Warn(_Header + "could not delete remote branch " + dock.BranchName + " for dock " + dock.Id + ": " + ex.Message);
                }
            }

            // Restore bare repo HEAD so subsequent git operations do not see a dangling ref.
            string defaultBranch = !String.IsNullOrEmpty(vessel?.DefaultBranch) ? vessel!.DefaultBranch : "main";
            try
            {
                await _Git.SetHeadSymbolicRefAsync(repoPath, "refs/heads/" + defaultBranch, token).ConfigureAwait(false);
                _Logging.Info(_Header + "restored bare repo HEAD to refs/heads/" + defaultBranch + " for dock " + dock.Id);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "bare repo HEAD restore failed for " + repoPath + " after dock " + dock.Id + " branch cleanup: " + ex.Message);
            }
        }

        private async Task PersistDockStartCommitAsync(string dockId, string headCommit, CancellationToken token)
        {
            string metadataDirectory = Path.Combine(_Settings.LogDirectory, "docks");
            Directory.CreateDirectory(metadataDirectory);
            await File.WriteAllTextAsync(Path.Combine(metadataDirectory, dockId + ".start"), headCommit + "\n", token).ConfigureAwait(false);
        }

        private void TryDeleteDockStartCommitFile(string dockId)
        {
            if (String.IsNullOrEmpty(dockId))
            {
                return;
            }

            try
            {
                string metadataPath = Path.Combine(_Settings.LogDirectory, "docks", dockId + ".start");
                if (File.Exists(metadataPath))
                {
                    File.Delete(metadataPath);
                }
            }
            catch (Exception ex)
            {
                _Logging.Debug(_Header + "could not remove dock start commit metadata for " + dockId + ": " + ex.Message);
            }
        }

        private async Task<bool> IsWorktreeOwnedByAnotherActiveDockAsync(Dock dock, CancellationToken token)
        {
            if (String.IsNullOrWhiteSpace(dock.WorktreePath)) return false;

            string dockPath = NormalizePath(dock.WorktreePath);
            List<Dock> vesselDocks = await _Database.Docks.EnumerateByVesselAsync(dock.VesselId, token).ConfigureAwait(false);
            return vesselDocks.Any(other =>
                other.Active &&
                !String.Equals(other.Id, dock.Id, StringComparison.OrdinalIgnoreCase) &&
                !String.IsNullOrWhiteSpace(other.WorktreePath) &&
                String.Equals(NormalizePath(other.WorktreePath!), dockPath, StringComparison.OrdinalIgnoreCase));
        }

        private static string NormalizePath(string path)
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        /// <summary>
        /// Provision each sibling repository declared by the vessel into the dock at its declared
        /// relative checkout path. Returns immediately when the vessel declares no siblings, leaving
        /// single-repo dock behavior unchanged. Each sibling source is resolved from a known Armada
        /// vessel (by ID then name) or a raw git URL, cloned bare once into the repos directory, and
        /// checked out as a worktree at the path the consumer's parent-probe arithmetic expects.
        /// </summary>
        private async Task ProvisionSiblingReposAsync(Vessel vessel, string worktreePath, string dockBranchName, CancellationToken token)
        {
            List<SiblingRepo> siblings = vessel.GetSiblingRepos();
            if (siblings.Count == 0) return;

            foreach (SiblingRepo sibling in siblings)
            {
                if (sibling == null || String.IsNullOrWhiteSpace(sibling.RelativePath))
                {
                    _Logging.Warn(_Header + "skipping sibling repo with empty relative path for vessel " + vessel.Id);
                    continue;
                }

                string? siblingRepoPath = await ResolveSiblingRepoPathAsync(sibling, token).ConfigureAwait(false);
                if (String.IsNullOrEmpty(siblingRepoPath))
                {
                    _Logging.Warn(_Header + "could not resolve source for sibling repo (relativePath=" + sibling.RelativePath + ") on vessel " + vessel.Id);
                    continue;
                }

                string siblingWorktreePath = Path.GetFullPath(Path.Combine(worktreePath, sibling.RelativePath));

                if (Directory.Exists(siblingWorktreePath))
                {
                    bool isRegistered = await _Git.IsWorktreeRegisteredAsync(siblingRepoPath, siblingWorktreePath, token).ConfigureAwait(false);
                    if (isRegistered)
                    {
                        try { await _Git.RemoveWorktreeAsync(siblingWorktreePath, token).ConfigureAwait(false); }
                        catch (Exception rmEx) { _Logging.Warn(_Header + "git worktree remove failed for sibling " + siblingWorktreePath + ": " + rmEx.Message); }
                    }
                    await ForceRemoveDirectoryAsync(siblingWorktreePath, token).ConfigureAwait(false);
                }

                // Branch-compat rule: when the strategy is MatchBranchElseDefault and the dock branch
                // already exists in the sibling repo, check out that same-named branch so the sibling
                // tracks the dock's work. Otherwise fall back to the sibling's declared default branch
                // (or "main"). DefaultOnly always uses the fallback.
                string fallbackBranch = !String.IsNullOrWhiteSpace(sibling.DefaultBranch) ? sibling.DefaultBranch! : "main";
                string siblingBranch = fallbackBranch;
                if (sibling.BranchStrategy == SiblingBranchStrategyEnum.MatchBranchElseDefault
                    && await _Git.BranchExistsAsync(siblingRepoPath, dockBranchName, token).ConfigureAwait(false))
                {
                    siblingBranch = dockBranchName;
                }

                await _Git.CreateWorktreeAsync(siblingRepoPath, siblingWorktreePath, siblingBranch, fallbackBranch, token).ConfigureAwait(false);
                _Logging.Info(_Header + "provisioned sibling repo at " + siblingWorktreePath + " (branch " + siblingBranch + ") for vessel " + vessel.Id);

                await ProvisionSiblingArtifactsAsync(sibling, siblingWorktreePath, token).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Copies git-ignored extraction artifact directories from the sibling vessel's host
        /// WorkingDirectory into the dock sibling worktree so consumer build probes resolve them.
        /// No-op when ExtractionArtifactPaths is empty or the sibling has no VesselRef.
        /// Skips cleanly with a warning when the source directory is absent on the host.
        /// </summary>
        private async Task ProvisionSiblingArtifactsAsync(SiblingRepo sibling, string siblingWorktreePath, CancellationToken token)
        {
            if (sibling.ExtractionArtifactPaths == null || sibling.ExtractionArtifactPaths.Count == 0) return;
            if (String.IsNullOrWhiteSpace(sibling.VesselRef)) return;

            Vessel? siblingVessel = null;
            try { siblingVessel = await _Database.Vessels.ReadAsync(sibling.VesselRef, token).ConfigureAwait(false); } catch { }
            if (siblingVessel == null)
            {
                try { siblingVessel = await _Database.Vessels.ReadByNameAsync(sibling.VesselRef, token).ConfigureAwait(false); } catch { }
            }

            if (siblingVessel == null || String.IsNullOrWhiteSpace(siblingVessel.WorkingDirectory))
            {
                _Logging.Warn(_Header + "skipping extraction artifact provisioning: vessel not found or WorkingDirectory not configured (vesselRef=" + sibling.VesselRef + ")");
                return;
            }

            foreach (string artifactPath in sibling.ExtractionArtifactPaths)
            {
                if (String.IsNullOrWhiteSpace(artifactPath)) continue;

                string sourceDir = Path.Combine(siblingVessel.WorkingDirectory, artifactPath);
                string destDir = Path.Combine(siblingWorktreePath, artifactPath);

                if (!Directory.Exists(sourceDir))
                {
                    _Logging.Warn(_Header + "extraction artifact source absent -- data-tests will be skipped until artifacts are generated (vesselRef=" + sibling.VesselRef + ", path=" + artifactPath + ")");
                    continue;
                }

                try
                {
                    CopyDirectoryRecursive(sourceDir, destDir);
                    _Logging.Info(_Header + "provisioned extraction artifacts from " + sourceDir + " to " + destDir);
                }
                catch (Exception ex)
                {
                    _Logging.Warn(_Header + "failed to copy extraction artifacts (vesselRef=" + sibling.VesselRef + ", path=" + artifactPath + "): " + ex.Message);
                }
            }
        }

        /// <summary>
        /// Recursively copies all files from sourceDir into destDir, creating subdirectories as needed.
        /// </summary>
        private static void CopyDirectoryRecursive(string sourceDir, string destDir)
        {
            Directory.CreateDirectory(destDir);
            foreach (string sourceFile in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(sourceDir, sourceFile);
                string destFile = Path.Combine(destDir, relative);
                string? destFileDir = Path.GetDirectoryName(destFile);
                if (!String.IsNullOrEmpty(destFileDir) && !Directory.Exists(destFileDir))
                {
                    Directory.CreateDirectory(destFileDir);
                }
                File.Copy(sourceFile, destFile, overwrite: true);
            }
        }

        /// <summary>
        /// Resolve and ensure the bare repository backing a sibling declaration exists locally,
        /// returning its path. Prefers a known Armada vessel referenced by ID or name; otherwise
        /// uses the declared git URL. Returns null when no source can be resolved.
        /// </summary>
        private async Task<string?> ResolveSiblingRepoPathAsync(SiblingRepo sibling, CancellationToken token)
        {
            Vessel? siblingVessel = null;
            if (!String.IsNullOrWhiteSpace(sibling.VesselRef))
            {
                try { siblingVessel = await _Database.Vessels.ReadAsync(sibling.VesselRef, token).ConfigureAwait(false); }
                catch { }
                if (siblingVessel == null)
                {
                    try { siblingVessel = await _Database.Vessels.ReadByNameAsync(sibling.VesselRef, token).ConfigureAwait(false); }
                    catch { }
                }
            }

            string? repoUrl = sibling.RepoUrl;
            string repoPath;
            if (siblingVessel != null)
            {
                repoPath = siblingVessel.LocalPath ?? Path.Combine(_Settings.ReposDirectory, siblingVessel.Name + ".git");
                if (String.IsNullOrWhiteSpace(repoUrl)) repoUrl = siblingVessel.RepoUrl;
            }
            else if (!String.IsNullOrWhiteSpace(repoUrl))
            {
                repoPath = Path.Combine(_Settings.ReposDirectory, DeriveRepoName(repoUrl) + ".git");
            }
            else
            {
                return null;
            }

            string normalizedRepoPath = Path.GetFullPath(repoPath);
            SemaphoreSlim repoLock = _RepoProvisionLocks.GetOrAdd(normalizedRepoPath, _ => new SemaphoreSlim(1, 1));
            await repoLock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (Directory.Exists(repoPath) && !await _Git.IsRepositoryAsync(repoPath, token).ConfigureAwait(false))
                {
                    _Logging.Warn(_Header + "removing corrupt sibling repo directory: " + repoPath);
                    await ForceRemoveDirectoryAsync(repoPath, token).ConfigureAwait(false);
                }

                if (!await _Git.IsRepositoryAsync(repoPath, token).ConfigureAwait(false))
                {
                    if (String.IsNullOrWhiteSpace(repoUrl)) return null;
                    await _Git.CloneBareAsync(repoUrl, repoPath, token).ConfigureAwait(false);
                    if (siblingVessel != null && String.IsNullOrEmpty(siblingVessel.LocalPath))
                    {
                        siblingVessel.LocalPath = repoPath;
                        try { await _Database.Vessels.UpdateAsync(siblingVessel, token).ConfigureAwait(false); }
                        catch (Exception ex) { _Logging.Warn(_Header + "could not persist LocalPath for sibling vessel " + siblingVessel.Id + ": " + ex.Message); }
                    }
                }
                else if (!String.IsNullOrWhiteSpace(repoUrl))
                {
                    try { await _Git.FetchAsync(repoPath, token).ConfigureAwait(false); }
                    catch (Exception fetchEx) { _Logging.Warn(_Header + "fetch failed for sibling repo " + repoPath + ", continuing with local state: " + fetchEx.Message); }
                }

                return repoPath;
            }
            finally
            {
                repoLock.Release();
            }
        }

        /// <summary>
        /// Tear down any sibling worktrees provisioned for a dock. Reads the parent vessel's declared
        /// siblings and removes each one at its relative path. No-op for vessels without siblings.
        /// </summary>
        private async Task RemoveSiblingReposForDockAsync(Dock dock, CancellationToken token)
        {
            if (dock == null || String.IsNullOrEmpty(dock.WorktreePath)) return;

            Vessel? vessel = null;
            try
            {
                vessel = !String.IsNullOrEmpty(dock.TenantId)
                    ? await _Database.Vessels.ReadAsync(dock.TenantId, dock.VesselId, token).ConfigureAwait(false)
                    : await _Database.Vessels.ReadAsync(dock.VesselId, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "could not read vessel " + dock.VesselId + " for sibling cleanup: " + ex.Message);
            }
            if (vessel == null) return;

            List<SiblingRepo> siblings = vessel.GetSiblingRepos();
            if (siblings.Count == 0) return;

            foreach (SiblingRepo sibling in siblings)
            {
                if (sibling == null || String.IsNullOrWhiteSpace(sibling.RelativePath)) continue;

                string siblingWorktreePath = Path.GetFullPath(Path.Combine(dock.WorktreePath, sibling.RelativePath));
                if (!Directory.Exists(siblingWorktreePath)) continue;

                try
                {
                    await _Git.RemoveWorktreeAsync(siblingWorktreePath, token).ConfigureAwait(false);
                    _Logging.Info(_Header + "removed sibling worktree for dock " + dock.Id + " at " + siblingWorktreePath);
                }
                catch (Exception ex)
                {
                    _Logging.Warn(_Header + "error removing sibling worktree for dock " + dock.Id + ": " + ex.Message);
                }

                await ForceRemoveDirectoryAsync(siblingWorktreePath, token).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Derive a stable repository directory name from a git URL by taking its last path segment
        /// and stripping a trailing ".git" suffix.
        /// </summary>
        private static string DeriveRepoName(string repoUrl)
        {
            string trimmed = repoUrl.Trim().TrimEnd('/');
            int lastSlash = trimmed.LastIndexOfAny(new[] { '/', ':' });
            string name = lastSlash >= 0 ? trimmed.Substring(lastSlash + 1) : trimmed;
            if (name.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
                name = name.Substring(0, name.Length - 4);
            return String.IsNullOrWhiteSpace(name) ? "sibling" : name;
        }

        /// <summary>
        /// Forcefully remove a directory with retry logic to handle locked files.
        /// On Windows, file handles from recently-exited processes can linger and
        /// cause Directory.Delete to fail. This method retries with increasing delays
        /// to give the OS time to release handles.
        /// </summary>
        /// <summary>
        /// Seed an empty bare repo with an initial commit containing a README.md.
        /// Uses a temporary clone to create the commit, then pushes to both the bare repo and the remote.
        /// </summary>
        private async Task SeedEmptyRepoAsync(Vessel vessel, string repoPath, CancellationToken token)
        {
            string tempPath = Path.Combine(Path.GetTempPath(), "armada_init_" + Guid.NewGuid().ToString("N"));
            try
            {
                // Use git init (not clone) because cloning an empty repo creates a broken state.
                Directory.CreateDirectory(tempPath);
                await RunGitInDirAsync(tempPath, "init", token).ConfigureAwait(false);
                await RunGitInDirAsync(tempPath, "checkout -b " + vessel.DefaultBranch, token).ConfigureAwait(false);

                // Create README.md
                string readmePath = Path.Combine(tempPath, "README.md");
                await File.WriteAllTextAsync(readmePath, "# " + vessel.Name + "\n", token).ConfigureAwait(false);

                // Commit
                await RunGitInDirAsync(tempPath, "add README.md", token).ConfigureAwait(false);
                await RunGitInDirAsync(tempPath, "commit -m \"Initial commit\"", token).ConfigureAwait(false);

                // Push to the remote
                if (!String.IsNullOrEmpty(vessel.RepoUrl))
                {
                    await RunGitInDirAsync(tempPath, "remote add origin " + vessel.RepoUrl, token).ConfigureAwait(false);
                    await RunGitInDirAsync(tempPath, "push -u origin " + vessel.DefaultBranch, token).ConfigureAwait(false);
                    _Logging.Info(_Header + "pushed initial commit to remote for " + vessel.Name);
                }

                // Delete the stale bare repo (if it exists) and re-clone fresh
                if (Directory.Exists(repoPath))
                {
                    await ForceRemoveDirectoryAsync(repoPath, token).ConfigureAwait(false);
                }
                await _Git.CloneBareAsync(vessel.RepoUrl ?? tempPath, repoPath, token).ConfigureAwait(false);

                _Logging.Info(_Header + "seeded empty repo for " + vessel.Name + " with initial commit");
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "failed to seed empty repo for " + vessel.Name + ": " + ex.Message);
                // Clean up any debris
                try { if (Directory.Exists(repoPath)) await ForceRemoveDirectoryAsync(repoPath, token).ConfigureAwait(false); }
                catch { }
                throw new InvalidOperationException("Repository " + vessel.Name + " is empty and could not be initialized: " + ex.Message);
            }
            finally
            {
                try { if (Directory.Exists(tempPath)) Directory.Delete(tempPath, true); }
                catch { }
            }
        }

        /// <summary>
        /// Run a git command in a specific directory.
        /// </summary>
        private async Task RunGitInDirAsync(string workDir, string arguments, CancellationToken token)
        {
            System.Diagnostics.ProcessStartInfo psi = new System.Diagnostics.ProcessStartInfo("git", arguments)
            {
                WorkingDirectory = workDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            System.Diagnostics.Process? proc = System.Diagnostics.Process.Start(psi);
            if (proc == null) throw new InvalidOperationException("Failed to start git " + arguments);
            await proc.WaitForExitAsync(token).ConfigureAwait(false);
            if (proc.ExitCode != 0)
            {
                string stderr = await proc.StandardError.ReadToEndAsync(token).ConfigureAwait(false);
                throw new InvalidOperationException("git " + arguments + " failed (exit " + proc.ExitCode + "): " + stderr.Trim());
            }
        }

        private async Task SeedDockMcpConfigAsync(Vessel vessel, string worktreePath, string? missionId, CancellationToken token)
        {
            if (String.IsNullOrWhiteSpace(worktreePath) || !Directory.Exists(worktreePath)) return;

            string rpcUrl = "http://localhost:" + _Settings.McpPort + "/rpc";
            string projectConfig = "{\n" +
                "  \"mcpServers\": {\n" +
                "    \"armada\": {\n" +
                "      \"type\": \"http\",\n" +
                "      \"url\": \"" + rpcUrl + "\"\n" +
                "    }\n" +
                "  }\n" +
                "}\n";

            string cursorConfig = "{\n" +
                "  \"mcpServers\": {\n" +
                "    \"armada\": {\n" +
                "      \"url\": \"" + rpcUrl + "\"\n" +
                "    }\n" +
                "  }\n" +
                "}\n";

            string codexConfig = "[mcp_servers.armada]\n" +
                "command = \"armada\"\n" +
                "args = [\"mcp\", \"stdio\"]\n" +
                "startup_timeout_sec = 120\n";

            string geminiConfig = "{\n" +
                "  \"mcpServers\": {\n" +
                "    \"armada\": {\n" +
                "      \"httpUrl\": \"" + rpcUrl + "\"\n" +
                "    }\n" +
                "  }\n" +
                "}\n";

            try
            {
                string projectMcpPath = Path.Combine(worktreePath, ".mcp.json");
                if (!File.Exists(projectMcpPath))
                {
                    await File.WriteAllTextAsync(projectMcpPath, projectConfig, token).ConfigureAwait(false);
                }

                string cursorDir = Path.Combine(worktreePath, ".cursor");
                Directory.CreateDirectory(cursorDir);
                string cursorMcpPath = Path.Combine(cursorDir, "mcp.json");
                if (!File.Exists(cursorMcpPath))
                {
                    await File.WriteAllTextAsync(cursorMcpPath, cursorConfig, token).ConfigureAwait(false);
                }

                string codexDir = Path.Combine(worktreePath, ".codex");
                Directory.CreateDirectory(codexDir);
                string codexMcpPath = Path.Combine(codexDir, "config.toml");
                if (!File.Exists(codexMcpPath))
                {
                    await File.WriteAllTextAsync(codexMcpPath, codexConfig, token).ConfigureAwait(false);
                }

                string geminiDir = Path.Combine(worktreePath, ".gemini");
                Directory.CreateDirectory(geminiDir);
                string geminiMcpPath = Path.Combine(geminiDir, "settings.json");
                if (!File.Exists(geminiMcpPath))
                {
                    await File.WriteAllTextAsync(geminiMcpPath, geminiConfig, token).ConfigureAwait(false);
                }

                // Seed the OpenCode reasonable-trust permission document so OpenCode captains
                // are not auto-rejected on external_directory access. Written for every dock
                // (like the per-runtime MCP configs above): runtimes other than OpenCode ignore
                // opencode.json, so this is inert for them. No-clobber: a pre-existing
                // opencode.json (operator- or captain-authored) is never overwritten.
                List<string> openCodeRoots = BuildOpenCodeGrantedRoots(vessel, worktreePath, missionId);
                string openCodeConfig = OpenCodePermissionConfigBuilder.Build(openCodeRoots);
                string openCodePath = Path.Combine(worktreePath, "opencode.json");
                if (!File.Exists(openCodePath))
                {
                    await File.WriteAllTextAsync(openCodePath, openCodeConfig, token).ConfigureAwait(false);
                }

                string? excludePath = ResolveGitInfoExcludePath(worktreePath);
                if (!String.IsNullOrWhiteSpace(excludePath))
                {
                    await EnsureGitExcludeEntryAsync(excludePath, ".mcp.json", token).ConfigureAwait(false);
                    await EnsureGitExcludeEntryAsync(excludePath, ".cursor/mcp.json", token).ConfigureAwait(false);
                    await EnsureGitExcludeEntryAsync(excludePath, ".codex/config.toml", token).ConfigureAwait(false);
                    await EnsureGitExcludeEntryAsync(excludePath, ".gemini/settings.json", token).ConfigureAwait(false);
                    await EnsureGitExcludeEntryAsync(excludePath, "opencode.json", token).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "could not seed dock MCP config for " + worktreePath + ": " + ex.Message);
            }
        }

        /// <summary>
        /// Derive the reasonable-trust directory roots granted to an OpenCode captain in this dock,
        /// all from settings/vessel state (never hardcoded). The roots are, in order:
        /// the dock worktree (the OpenCode project root, which also covers the in-dock _briefing/
        /// context pack and .armada/playbooks/ materializations); each declared sibling vessel
        /// repository checkout (so cross-repo csproj build probes resolve); and the Admiral-staged
        /// playbooks directory for this mission (outside the dock). Whitespace/blanket roots are
        /// dropped downstream by <see cref="OpenCodePermissionConfigBuilder"/>, so a single-repo
        /// vessel with no mission id simply yields the worktree root.
        /// </summary>
        private List<string> BuildOpenCodeGrantedRoots(Vessel vessel, string worktreePath, string? missionId)
        {
            List<string> roots = new List<string>();

            if (!String.IsNullOrWhiteSpace(worktreePath))
            {
                roots.Add(Path.GetFullPath(worktreePath));
            }

            if (vessel != null)
            {
                foreach (SiblingRepo sibling in vessel.GetSiblingRepos())
                {
                    if (sibling == null || String.IsNullOrWhiteSpace(sibling.RelativePath)) continue;
                    roots.Add(Path.GetFullPath(Path.Combine(worktreePath, sibling.RelativePath)));
                }
            }

            if (!String.IsNullOrWhiteSpace(missionId) && !String.IsNullOrWhiteSpace(_Settings.LogDirectory))
            {
                roots.Add(Path.GetFullPath(Path.Combine(_Settings.LogDirectory, "playbooks", missionId)));
            }

            return roots;
        }

        private static string? ResolveGitInfoExcludePath(string worktreePath)
        {
            string dotGitPath = Path.Combine(worktreePath, ".git");
            if (Directory.Exists(dotGitPath))
            {
                return Path.Combine(dotGitPath, "info", "exclude");
            }

            if (!File.Exists(dotGitPath)) return null;

            string content = File.ReadAllText(dotGitPath).Trim();
            const string prefix = "gitdir:";
            if (!content.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;

            string gitDir = content.Substring(prefix.Length).Trim();
            if (!Path.IsPathRooted(gitDir))
            {
                gitDir = Path.GetFullPath(Path.Combine(worktreePath, gitDir));
            }

            return Path.Combine(gitDir, "info", "exclude");
        }

        private static async Task EnsureGitExcludeEntryAsync(string excludePath, string entry, CancellationToken token)
        {
            if (String.IsNullOrWhiteSpace(excludePath) || String.IsNullOrWhiteSpace(entry)) return;

            string? dir = Path.GetDirectoryName(excludePath);
            if (!String.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);

            string existing = File.Exists(excludePath)
                ? await File.ReadAllTextAsync(excludePath, token).ConfigureAwait(false)
                : "";

            string normalizedEntry = entry.Trim().Replace("\\", "/");
            bool alreadyPresent = existing
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Any(line => String.Equals(line.Trim(), normalizedEntry, StringComparison.OrdinalIgnoreCase));

            if (alreadyPresent) return;

            string prefix = existing.Length > 0 && !existing.EndsWith("\n", StringComparison.Ordinal) ? "\n" : "";
            await File.AppendAllTextAsync(excludePath, prefix + normalizedEntry + "\n", token).ConfigureAwait(false);
        }

        private async Task ForceRemoveDirectoryAsync(string path, CancellationToken token)
        {
            const int maxAttempts = 5;
            int[] delayMs = { 0, 500, 1000, 2000, 3000 };

            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                if (!Directory.Exists(path)) return;

                if (attempt > 0)
                {
                    _Logging.Debug(_Header + "retry " + attempt + "/" + (maxAttempts - 1) + " removing directory: " + path);
                    await Task.Delay(delayMs[attempt], token).ConfigureAwait(false);
                }

                try
                {
                    // Clear read-only attributes that can block deletion on Windows.
                    // On Unix this is unnecessary (read-only attr maps to file permissions
                    // and Directory.Delete handles it), so skip the expensive enumeration.
                    if (OperatingSystem.IsWindows())
                    {
                        foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                        {
                            try
                            {
                                FileAttributes attrs = File.GetAttributes(file);
                                if ((attrs & FileAttributes.ReadOnly) != 0)
                                    File.SetAttributes(file, attrs & ~FileAttributes.ReadOnly);
                            }
                            catch { }
                        }
                    }

                    Directory.Delete(path, recursive: true);
                }
                catch (Exception ex) when (attempt < maxAttempts - 1)
                {
                    _Logging.Debug(_Header + "directory delete attempt " + (attempt + 1) + " failed for " + path + ": " + ex.Message);
                    continue;
                }
                catch (Exception ex)
                {
                    _Logging.Warn(_Header + "failed to remove directory after " + maxAttempts + " attempts: " + path + ": " + ex.Message);
                    return;
                }
            }
        }

        #endregion
    }
}
