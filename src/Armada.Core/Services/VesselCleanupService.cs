namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using SyslogLogging;

    /// <summary>
    /// Vessel cleanup: a read-only inventory of a vessel's docks, worktrees, and Armada-managed local branches,
    /// classified as safe, in use, or unknown, and a cleanup that revalidates each selected resource immediately before
    /// removing it with the existing dock and git primitives. Resources are inspected on the host that owns them (the
    /// Admiral or a Harbor); a host that cannot be reached makes its resources unknown. Remote branches, default
    /// branches, primary checkouts, and paths outside Armada's docks are never removed.
    /// </summary>
    public class VesselCleanupService
    {
        #region Public-Members

        /// <summary>
        /// Prefix of branches Armada creates and may garbage collect.
        /// </summary>
        public const string ManagedBranchPrefix = "armada/";

        /// <summary>
        /// Host key used in resource keys for the Admiral host.
        /// </summary>
        public const string AdmiralHostKey = "admiral";

        /// <summary>
        /// Test hook: resolve a Harbor host (null = not reachable). When unset, a Harbor is reachable only while it is
        /// connected through <see cref="DockHostResolver.Harbors"/>.
        /// </summary>
        public Func<string, DockHost?>? HarborHostOverride { get; set; } = null;

        #endregion

        #region Private-Members

        private readonly string _Header = "[VesselCleanupService] ";
        private readonly DatabaseDriver _Database;
        private readonly ArmadaSettings _Settings;
        private readonly IDockService _Docks;
        private readonly DockHostResolver _Hosts;
        private readonly LoggingModule _Logging;

        private static readonly HashSet<MissionStatusEnum> _ActiveMissionStatuses = new HashSet<MissionStatusEnum>
        {
            MissionStatusEnum.Pending, MissionStatusEnum.Assigned, MissionStatusEnum.InProgress, MissionStatusEnum.WorkProduced,
            MissionStatusEnum.PullRequestOpen, MissionStatusEnum.Testing, MissionStatusEnum.Review, MissionStatusEnum.Blocked,
            MissionStatusEnum.Verifying
        };

        private static readonly HashSet<string> _ProtectedBranchNames = new HashSet<string>(StringComparer.Ordinal) { "main", "master", "develop", "trunk" };

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="settings">Settings (docks directory).</param>
        /// <param name="docks">Dock service used to purge docks.</param>
        /// <param name="hosts">Dock host resolver (Admiral host and Harbors).</param>
        /// <param name="logging">Logging module.</param>
        /// <exception cref="ArgumentNullException">Thrown when any argument is null.</exception>
        public VesselCleanupService(DatabaseDriver database, ArmadaSettings settings, IDockService docks, DockHostResolver hosts, LoggingModule logging)
        {
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _Docks = docks ?? throw new ArgumentNullException(nameof(docks));
            _Hosts = hosts ?? throw new ArgumentNullException(nameof(hosts));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Inventory a vessel's cleanup candidates. Read-only: nothing is created, changed, or deleted.
        /// </summary>
        /// <param name="vessel">Vessel (already authorized by the caller).</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The classified inventory.</returns>
        /// <exception cref="ArgumentNullException">Thrown when vessel is null.</exception>
        public async Task<VesselCleanupInventory> ScanAsync(Vessel vessel, CancellationToken token = default)
        {
            if (vessel == null) throw new ArgumentNullException(nameof(vessel));
            List<VesselCleanupResource> resources = await InventoryAsync(vessel, null, token).ConfigureAwait(false);
            return BuildInventory(vessel, resources);
        }

        /// <summary>
        /// Clean up the selected resources. Each one is re-inventoried immediately before removal and removed only when
        /// it is still classified safe; docks run first, then orphaned worktrees, then branches, so a branch freed by its
        /// dock in the same request can follow it. Failures are reported per item and do not stop the others.
        /// </summary>
        /// <param name="vessel">Vessel (already authorized by the caller).</param>
        /// <param name="request">Selected resource keys.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Per-item outcomes and the refreshed inventory.</returns>
        /// <exception cref="ArgumentNullException">Thrown when vessel or request is null.</exception>
        /// <exception cref="ArgumentException">Thrown when no keys are selected.</exception>
        public async Task<VesselCleanupResult> CleanupAsync(Vessel vessel, VesselCleanupRequest request, CancellationToken token = default)
        {
            if (vessel == null) throw new ArgumentNullException(nameof(vessel));
            if (request == null) throw new ArgumentNullException(nameof(request));
            List<string> keys = (request.Keys ?? new List<string>())
                .Where(k => !String.IsNullOrWhiteSpace(k))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(KeyOrder)
                .ToList();
            if (keys.Count == 0) throw new ArgumentException("Select at least one resource to clean up.");

            VesselCleanupResult result = new VesselCleanupResult();
            foreach (string key in keys)
            {
                token.ThrowIfCancellationRequested();
                VesselCleanupItemResult item = await CleanupOneAsync(vessel, key, token).ConfigureAwait(false);
                result.Results.Add(item);
                if (item.Status == VesselCleanupItemStatusEnum.Deleted) result.Deleted++;
                else if (item.Status == VesselCleanupItemStatusEnum.Failed) result.Failed++;
                else result.Skipped++;
            }

            result.Inventory = await ScanAsync(vessel, token).ConfigureAwait(false);
            return result;
        }

        #endregion

        #region Private-Methods

        private async Task<VesselCleanupItemResult> CleanupOneAsync(Vessel vessel, string key, CancellationToken token)
        {
            VesselCleanupItemResult item = new VesselCleanupItemResult { Key = key };
            VesselCleanupResource? current;
            try
            {
                // Revalidate against fresh state: concurrent dispatch, landing, or edits since the scan win.
                List<VesselCleanupResource> fresh = await InventoryAsync(vessel, key, token).ConfigureAwait(false);
                current = fresh.FirstOrDefault(r => r.Key == key);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                item.Status = VesselCleanupItemStatusEnum.Failed;
                item.Message = "Revalidation failed: " + ex.Message;
                return item;
            }

            if (current == null)
            {
                item.Message = "No longer present; nothing to clean up.";
                return item;
            }

            item.ResourceType = current.ResourceType;
            item.Identity = current.Identity;
            if (current.Disposition != VesselCleanupDispositionEnum.Safe)
            {
                item.Message = "No longer safe (" + current.Disposition + "): " + current.Reason;
                return item;
            }

            try
            {
                string? failure = await RemoveAsync(vessel, current, token).ConfigureAwait(false);
                if (failure != null)
                {
                    item.Status = VesselCleanupItemStatusEnum.Failed;
                    item.Message = failure;
                }
                else
                {
                    item.Status = VesselCleanupItemStatusEnum.Deleted;
                    item.Message = "Removed.";
                    _Logging.Info(_Header + "vessel " + vessel.Id + ": removed " + current.ResourceType + " " + current.Identity);
                }
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                item.Status = VesselCleanupItemStatusEnum.Failed;
                item.Message = ex.Message;
                _Logging.Warn(_Header + "vessel " + vessel.Id + ": could not remove " + current.ResourceType + " " + current.Identity + ": " + ex.Message);
            }

            return item;
        }

        private async Task<string?> RemoveAsync(Vessel vessel, VesselCleanupResource resource, CancellationToken token)
        {
            DockHost? host = ResolveHost(resource.HarborId);
            if (host == null) return "Harbor " + resource.HarborId + " is not connected.";

            if (resource.ResourceType == VesselCleanupResourceTypeEnum.Dock)
            {
                await _Docks.PurgeAsync(resource.DockId!, null, token).ConfigureAwait(false);
                if (!String.IsNullOrEmpty(resource.Path) && await host.Files.DirectoryExistsAsync(resource.Path, token).ConfigureAwait(false))
                    return "Dock record removed, but its worktree is still present at " + resource.Path + "; it now appears as an orphaned worktree.";
                return null;
            }

            if (resource.ResourceType == VesselCleanupResourceTypeEnum.Worktree)
            {
                if (!await host.Files.DirectoryExistsAsync(resource.Path!, token).ConfigureAwait(false))
                {
                    await host.Git.PruneWorktreesAsync(resource.RepositoryPath!, token).ConfigureAwait(false);
                }
                else if (host.IsHarbor)
                {
                    HarborConnectionManager? harbors = _Hosts.Harbors;
                    if (harbors == null) return "Harbor delegation is not available on this Admiral.";
                    Dock transient = new Dock(vessel.Id) { HarborId = resource.HarborId, WorktreePath = resource.Path, RepositoryPath = resource.RepositoryPath };
                    Armada.Core.Harbor.HarborDockResult? removed = await new HarborDockClient(harbors).ReclaimAsync(transient, vessel, token).ConfigureAwait(false);
                    if (removed == null || !removed.Success) return "Harbor did not remove the worktree: " + (removed?.Message ?? "no reply");
                }
                else
                {
                    await host.Git.RemoveWorktreeAsync(resource.Path!, token).ConfigureAwait(false);
                    await host.Git.PruneWorktreesAsync(resource.RepositoryPath!, token).ConfigureAwait(false);
                }

                if (await host.Files.DirectoryExistsAsync(resource.Path!, token).ConfigureAwait(false))
                    return "Worktree is still present at " + resource.Path + ".";
                return null;
            }

            if (resource.ResourceType == VesselCleanupResourceTypeEnum.Branch)
            {
                await host.Git.DeleteLocalBranchAsync(resource.RepositoryPath!, resource.BranchName!, token).ConfigureAwait(false);
                if (await host.Git.BranchExistsAsync(resource.RepositoryPath!, resource.BranchName!, token).ConfigureAwait(false))
                    return "Branch " + resource.BranchName + " still exists.";
                return null;
            }

            return "This resource type cannot be cleaned up.";
        }

        private async Task<List<VesselCleanupResource>> InventoryAsync(Vessel vessel, string? onlyKey, CancellationToken token)
        {
            CleanupSnapshot snap = await LoadSnapshotAsync(vessel, token).ConfigureAwait(false);
            List<VesselCleanupResource> resources = new List<VesselCleanupResource>();

            // Repositories to inspect: the Admiral's bare clone plus each Harbor repository a dock was created from.
            List<RepoState> repos = new List<RepoState>();
            if (!String.IsNullOrWhiteSpace(vessel.LocalPath) && Directory.Exists(vessel.LocalPath))
                repos.Add(new RepoState { HarborId = null, Path = vessel.LocalPath! });
            foreach (Dock dock in snap.VesselDocks)
            {
                if (!DockHostResolver.IsHarborDock(dock)) continue;
                if (repos.Any(r => r.HarborId == dock.HarborId && SamePath(r.Path, dock.RepositoryPath!, true))) continue;
                repos.Add(new RepoState { HarborId = dock.HarborId, Path = dock.RepositoryPath! });
            }

            foreach (RepoState repo in repos)
            {
                repo.Host = ResolveHost(repo.HarborId);
                if (repo.Host == null) continue;
                try
                {
                    repo.Worktrees = await repo.Host.Git.ListWorktreeEntriesAsync(repo.Path, token).ConfigureAwait(false);
                }
                catch (Exception ex) when (!(ex is OperationCanceledException))
                {
                    repo.Error = "could not list worktrees: " + ex.Message;
                }
                try
                {
                    repo.Branches = await repo.Host.Git.ListBranchTipsAsync(repo.Path, vessel.DefaultBranch, token).ConfigureAwait(false);
                }
                catch (Exception ex) when (!(ex is OperationCanceledException))
                {
                    repo.Error = (repo.Error == null ? "" : repo.Error + "; ") + "could not list branches: " + ex.Message;
                }
            }

            foreach (Dock dock in snap.VesselDocks)
            {
                string key = "dock:" + dock.Id;
                if (onlyKey != null && onlyKey != key) continue;
                resources.Add(await ClassifyDockAsync(vessel, dock, key, snap, repos, token).ConfigureAwait(false));
            }

            foreach (RepoState repo in repos)
            {
                string hostKey = repo.HarborId ?? AdmiralHostKey;
                if (repo.Host == null || repo.Error != null)
                {
                    string key = "repository:" + hostKey + "|" + repo.Path;
                    if (onlyKey != null && onlyKey != key) continue;
                    resources.Add(new VesselCleanupResource
                    {
                        Key = key,
                        ResourceType = VesselCleanupResourceTypeEnum.Repository,
                        Disposition = VesselCleanupDispositionEnum.Unknown,
                        Identity = repo.Path,
                        Path = repo.Path,
                        RepositoryPath = repo.Path,
                        HarborId = repo.HarborId,
                        Reason = repo.Host == null
                            ? "Harbor " + repo.HarborId + " is not connected; its worktrees and branches were not inspected."
                            : "Repository could not be inspected (" + repo.Error + "); its worktrees and branches are not classified."
                    });
                    if (repo.Host == null) continue;
                }

                foreach (GitWorktreeEntry entry in repo.Worktrees ?? new List<GitWorktreeEntry>())
                {
                    if (entry.IsBare || String.IsNullOrWhiteSpace(entry.Path)) continue;
                    if (SamePath(entry.Path, repo.Path, repo.HarborId == null)) continue;
                    if (IsPrimaryCheckout(vessel, entry.Path, repo.HarborId == null)) continue;
                    if (snap.AllDocks.Any(d => d.HarborId == repo.HarborId && !String.IsNullOrEmpty(d.WorktreePath) && SamePath(d.WorktreePath!, entry.Path, repo.HarborId == null))) continue;
                    string key = "worktree:" + hostKey + "|" + entry.Path;
                    if (onlyKey != null && onlyKey != key) continue;
                    resources.Add(await ClassifyOrphanWorktreeAsync(vessel, entry, key, repo, snap, token).ConfigureAwait(false));
                }

                foreach (BranchInfo branch in repo.Branches ?? new List<BranchInfo>())
                {
                    if (!IsManagedBranch(vessel, branch)) continue;
                    string key = "branch:" + hostKey + "|" + repo.Path + "|" + branch.Name;
                    if (onlyKey != null && onlyKey != key) continue;
                    resources.Add(await ClassifyBranchAsync(vessel, branch, key, repo, snap, token).ConfigureAwait(false));
                }
            }

            return resources;
        }

        private async Task<VesselCleanupResource> ClassifyDockAsync(Vessel vessel, Dock dock, string key, CleanupSnapshot snap, List<RepoState> repos, CancellationToken token)
        {
            bool local = !DockHostResolver.IsHarborDock(dock);
            VesselCleanupResource r = new VesselCleanupResource
            {
                Key = key,
                ResourceType = VesselCleanupResourceTypeEnum.Dock,
                Identity = dock.Id,
                DockId = dock.Id,
                Path = dock.WorktreePath,
                BranchName = dock.BranchName,
                RepositoryPath = local ? vessel.LocalPath : dock.RepositoryPath,
                HarborId = local ? null : dock.HarborId,
                CaptainId = dock.CaptainId
            };

            List<Mission> missions = snap.Missions
                .Where(m => m.DockId == dock.Id || (!String.IsNullOrEmpty(dock.BranchName) && m.BranchName == dock.BranchName))
                .OrderByDescending(m => m.LastUpdateUtc)
                .ToList();
            AttachMission(r, missions);

            // In-use evidence comes from the database and needs no host access.
            if (dock.Active && !String.IsNullOrEmpty(dock.CaptainId))
                return Classify(r, VesselCleanupDispositionEnum.InUse, "Dock is active and held by captain " + dock.CaptainId + ".");
            Captain? holder = snap.Captains.FirstOrDefault(c => c.CurrentDockId == dock.Id);
            if (holder != null)
                return Classify(r, VesselCleanupDispositionEnum.InUse, "Captain " + holder.Id + " (" + holder.State + ") is using this dock.");
            if (dock.State == DockStateEnum.Provisioning || dock.State == DockStateEnum.Leased || dock.State == DockStateEnum.Reclaiming)
                return Classify(r, VesselCleanupDispositionEnum.InUse, "Dock is " + dock.State + ".");
            if (dock.Active)
                return Classify(r, VesselCleanupDispositionEnum.InUse, "Dock is still active (not yet reclaimed); it may be reused.");
            string? use = await DescribeWorkReferenceAsync(missions, dock.BranchName, snap, token).ConfigureAwait(false);
            if (use != null) return Classify(r, VesselCleanupDispositionEnum.InUse, use);

            if (!String.IsNullOrEmpty(dock.WorktreePath))
            {
                Dock? sharer = snap.AllDocks.FirstOrDefault(d => d.Id != dock.Id && d.HarborId == dock.HarborId
                    && !String.IsNullOrEmpty(d.WorktreePath) && SamePath(d.WorktreePath!, dock.WorktreePath!, local));
                if (sharer != null)
                {
                    return sharer.Active
                        ? Classify(r, VesselCleanupDispositionEnum.InUse, "Worktree path is shared with active dock " + sharer.Id + ".")
                        : Classify(r, VesselCleanupDispositionEnum.Unknown, "Worktree path is shared with dock " + sharer.Id + "; ownership is ambiguous.");
                }

                string? unsafePath = CheckManagedPath(vessel, dock.WorktreePath!, local, dock);
                if (unsafePath != null) return Classify(r, VesselCleanupDispositionEnum.Unknown, unsafePath);
            }

            DockHost? host = ResolveHost(r.HarborId);
            if (host == null)
                return Classify(r, VesselCleanupDispositionEnum.Unknown, "Harbor " + r.HarborId + " is not connected; the worktree cannot be inspected.");

            if (String.IsNullOrEmpty(dock.WorktreePath))
                return Classify(r, VesselCleanupDispositionEnum.Safe, "Inactive dock record with no worktree" + MissionSuffix(r) + ".");

            bool exists;
            try
            {
                exists = await host.Files.DirectoryExistsAsync(dock.WorktreePath!, token).ConfigureAwait(false);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                return Classify(r, VesselCleanupDispositionEnum.Unknown, "Could not check the worktree: " + ex.Message);
            }

            if (!exists)
                return Classify(r, VesselCleanupDispositionEnum.Safe, "Inactive dock record; its worktree no longer exists" + MissionSuffix(r) + ".");

            if (local) r.SizeBytes = MeasureDirectory(dock.WorktreePath!);
            RepoState? repo = repos.FirstOrDefault(x => x.HarborId == r.HarborId && x.Path != null && r.RepositoryPath != null && SamePath(x.Path, r.RepositoryPath, local));
            string? risk = await DescribeWorktreeRiskAsync(vessel, host, dock.WorktreePath!, repo, token).ConfigureAwait(false);
            if (risk != null) return Classify(r, VesselCleanupDispositionEnum.Unknown, risk);
            return Classify(r, VesselCleanupDispositionEnum.Safe, "Inactive dock; worktree is clean and its commits are merged or kept on a branch" + MissionSuffix(r) + ".");
        }

        private async Task<VesselCleanupResource> ClassifyOrphanWorktreeAsync(Vessel vessel, GitWorktreeEntry entry, string key, RepoState repo, CleanupSnapshot snap, CancellationToken token)
        {
            bool local = repo.HarborId == null;
            string? branch = BranchFromRef(entry.BranchRef);
            VesselCleanupResource r = new VesselCleanupResource
            {
                Key = key,
                ResourceType = VesselCleanupResourceTypeEnum.Worktree,
                Identity = entry.Path,
                Path = entry.Path,
                BranchName = branch,
                RepositoryPath = repo.Path,
                HarborId = repo.HarborId
            };

            List<Mission> missions = branch == null ? new List<Mission>() : snap.Missions.Where(m => m.BranchName == branch).OrderByDescending(m => m.LastUpdateUtc).ToList();
            AttachMission(r, missions);
            string? use = await DescribeWorkReferenceAsync(missions, branch, snap, token).ConfigureAwait(false);
            if (use != null) return Classify(r, VesselCleanupDispositionEnum.InUse, use);
            Captain? holder = branch == null ? null : snap.Captains.FirstOrDefault(c => c.State != CaptainStateEnum.Idle && missions.Any(m => m.Id == c.CurrentMissionId));
            if (holder != null) return Classify(r, VesselCleanupDispositionEnum.InUse, "Captain " + holder.Id + " is working on this branch.");

            string? unsafePath = CheckManagedPath(vessel, entry.Path, local, null);
            if (unsafePath == null && !local)
            {
                // A Harbor's docks folder is not known here: accept only siblings of this vessel's own dock worktrees.
                string? parent = ParentOf(entry.Path);
                bool sibling = parent != null && snap.VesselDocks.Any(d => d.HarborId == repo.HarborId && !String.IsNullOrEmpty(d.WorktreePath)
                    && ParentOf(d.WorktreePath!) is string p && SamePath(p, parent, false));
                if (!sibling) unsafePath = "Worktree is not next to this vessel's Harbor docks; ownership is ambiguous.";
            }
            if (unsafePath != null) return Classify(r, VesselCleanupDispositionEnum.Unknown, unsafePath);

            bool exists;
            try
            {
                exists = await repo.Host!.Files.DirectoryExistsAsync(entry.Path, token).ConfigureAwait(false);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                return Classify(r, VesselCleanupDispositionEnum.Unknown, "Could not check the worktree: " + ex.Message);
            }
            if (!exists)
                return Classify(r, VesselCleanupDispositionEnum.Safe, "Stale worktree registration with no dock record; the directory no longer exists (prune).");

            if (local) r.SizeBytes = MeasureDirectory(entry.Path);
            string? risk = await DescribeWorktreeRiskAsync(vessel, repo.Host!, entry.Path, repo, token).ConfigureAwait(false);
            if (risk != null) return Classify(r, VesselCleanupDispositionEnum.Unknown, risk);
            return Classify(r, VesselCleanupDispositionEnum.Safe, "Orphaned worktree with no dock record; clean, and its commits are merged or kept on a branch.");
        }

        private async Task<VesselCleanupResource> ClassifyBranchAsync(Vessel vessel, BranchInfo branch, string key, RepoState repo, CleanupSnapshot snap, CancellationToken token)
        {
            VesselCleanupResource r = new VesselCleanupResource
            {
                Key = key,
                ResourceType = VesselCleanupResourceTypeEnum.Branch,
                Identity = branch.Name,
                BranchName = branch.Name,
                RepositoryPath = repo.Path,
                Path = repo.Path,
                HarborId = repo.HarborId
            };

            List<Mission> missions = snap.Missions.Where(m => m.BranchName == branch.Name).OrderByDescending(m => m.LastUpdateUtc).ToList();
            AttachMission(r, missions);

            GitWorktreeEntry? checkedOut = (repo.Worktrees ?? new List<GitWorktreeEntry>()).FirstOrDefault(w => BranchFromRef(w.BranchRef) == branch.Name);
            if (checkedOut != null)
                return Classify(r, VesselCleanupDispositionEnum.InUse, "Checked out in worktree " + checkedOut.Path + ".");
            if (repo.Worktrees == null)
                return Classify(r, VesselCleanupDispositionEnum.Unknown, "Worktrees of this repository could not be listed, so the branch may be checked out.");
            Dock? dockRef = snap.VesselDocks.FirstOrDefault(d => d.BranchName == branch.Name && d.HarborId == repo.HarborId);
            if (dockRef != null)
            {
                r.DockId = dockRef.Id;
                return Classify(r, VesselCleanupDispositionEnum.InUse, "Dock " + dockRef.Id + " still references this branch; clean up the dock first.");
            }
            string? use = await DescribeWorkReferenceAsync(missions, branch.Name, snap, token).ConfigureAwait(false);
            if (use != null) return Classify(r, VesselCleanupDispositionEnum.InUse, use);
            if (branch.Name.StartsWith(ManagedBranchPrefix + "merge-queue/", StringComparison.Ordinal))
            {
                MergeEntry? pending = snap.MergeEntries.FirstOrDefault(e => IsLiveMergeEntry(e));
                if (pending != null)
                    return Classify(r, VesselCleanupDispositionEnum.InUse, "Merge-queue integration branch while merge entry " + pending.Id + " is " + pending.Status + ".");
            }

            bool? merged = await IsMergedAsync(repo.Host!.Git, repo.Path, branch.Name, vessel.DefaultBranch, token).ConfigureAwait(false);
            if (merged == null)
                return Classify(r, VesselCleanupDispositionEnum.Unknown, "Could not compare the branch with " + vessel.DefaultBranch + ".");
            if (merged == false)
                return Classify(r, VesselCleanupDispositionEnum.Unknown, "Has commits that are not on " + vessel.DefaultBranch + " (unmerged work); local branch only, remote untouched.");
            return Classify(r, VesselCleanupDispositionEnum.Safe, "Fully merged into " + vessel.DefaultBranch + " and not used by any work" + MissionSuffix(r) + "; deletes the local branch only.");
        }

        /// <summary>
        /// Why a worktree directory cannot be proven disposable, or null when it is clean and its HEAD is merged into the
        /// default branch or is the tip of a branch that cleanup keeps.
        /// </summary>
        private async Task<string?> DescribeWorktreeRiskAsync(Vessel vessel, DockHost host, string worktreePath, RepoState? repo, CancellationToken token)
        {
            GitWorkingTreeStatus status;
            try
            {
                status = await host.Git.GetWorkingTreeStatusAsync(worktreePath, token).ConfigureAwait(false);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                return "Could not read the worktree status (" + ex.Message + "); it may hold uncommitted work.";
            }
            if (!status.IsClean)
                return "Worktree has uncommitted changes (" + status.ModifiedCount + " modified, " + status.UntrackedCount + " untracked).";

            string? head;
            try
            {
                head = await host.Git.GetHeadCommitHashAsync(worktreePath, token).ConfigureAwait(false);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                return "Could not read the worktree HEAD: " + ex.Message;
            }
            if (String.IsNullOrWhiteSpace(head)) return "Could not read the worktree HEAD.";

            bool? merged = await IsMergedAsync(host.Git, worktreePath, head!, vessel.DefaultBranch, token).ConfigureAwait(false);
            if (merged == true) return null;

            string? branch;
            try
            {
                branch = await host.Git.GetCurrentBranchAsync(worktreePath, token).ConfigureAwait(false);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                branch = null;
                _Logging.Debug(_Header + "could not read the branch of " + worktreePath + ": " + ex.Message);
            }
            if (!String.IsNullOrWhiteSpace(branch) && branch != "HEAD" && repo?.Host != null)
            {
                string? tip = null;
                try
                {
                    tip = await repo.Host.Git.ResolveBranchTipAsync(repo.Path, branch!, token).ConfigureAwait(false);
                }
                catch (Exception ex) when (!(ex is OperationCanceledException))
                {
                    _Logging.Debug(_Header + "could not resolve " + branch + ": " + ex.Message);
                }
                if (tip != null && String.Equals(tip.Trim(), head!.Trim(), StringComparison.OrdinalIgnoreCase)) return null;
            }

            return merged == null
                ? "Could not prove the worktree's commits are merged into " + vessel.DefaultBranch + "."
                : "Worktree HEAD has commits that are neither merged into " + vessel.DefaultBranch + " nor kept on a branch.";
        }

        /// <summary>
        /// Reason the given missions, branch, or merge queue still need the resource, or null.
        /// </summary>
        private async Task<string?> DescribeWorkReferenceAsync(List<Mission> missions, string? branch, CleanupSnapshot snap, CancellationToken token)
        {
            foreach (Mission m in missions)
            {
                if (_ActiveMissionStatuses.Contains(m.Status))
                    return "Mission " + m.Id + " is " + m.Status + ".";
                if (m.Status == MissionStatusEnum.LandingFailed)
                    return "Mission " + m.Id + " is LandingFailed; landing can still be retried.";
            }

            foreach (MergeEntry e in snap.MergeEntries)
            {
                bool refers = (!String.IsNullOrEmpty(branch) && e.BranchName == branch)
                    || (!String.IsNullOrEmpty(e.MissionId) && missions.Any(m => m.Id == e.MissionId));
                if (!refers) continue;
                if (IsLiveMergeEntry(e))
                    return "Merge-queue entry " + e.Id + " is " + e.Status + ".";
                if (e.Status == MergeStatusEnum.Failed)
                    return "Merge-queue entry " + e.Id + " failed and can still be retried; resolve or remove it first.";
            }

            foreach (string voyageId in missions.Where(m => !String.IsNullOrEmpty(m.VoyageId)).Select(m => m.VoyageId!).Distinct())
            {
                Voyage? voyage = await _Database.Voyages.ReadAsync(voyageId, token).ConfigureAwait(false);
                if (voyage != null && (voyage.Status == VoyageStatusEnum.Open || voyage.Status == VoyageStatusEnum.InProgress))
                    return "Voyage " + voyage.Id + " is " + voyage.Status + " and may still use this work.";
            }

            return null;
        }

        /// <summary>
        /// Why a path is not a disposable Armada dock path, or null when it is.
        /// </summary>
        private string? CheckManagedPath(Vessel vessel, string path, bool local, Dock? dock)
        {
            if (IsPrimaryCheckout(vessel, path, local)) return "Path is the vessel's primary checkout or repository; it is never removed.";
            if (dock != null)
            {
                foreach (string? protectedPath in new[] { dock.RepositoryPath, dock.CheckoutPath })
                {
                    if (!String.IsNullOrWhiteSpace(protectedPath) && (SamePath(path, protectedPath!, local) || IsUnder(protectedPath!, path, local)))
                        return "Path is or contains the repository checkout " + protectedPath + "; it is never removed.";
                }
            }
            if (!local) return null;

            string docks = _Settings.DocksDirectory;
            if (String.IsNullOrWhiteSpace(docks) || !IsUnder(path, docks, true))
                return "Path is outside Armada's docks directory; ownership is ambiguous.";
            return null;
        }

        private bool IsPrimaryCheckout(Vessel vessel, string path, bool local)
        {
            foreach (string? p in new[] { vessel.LocalPath, vessel.WorkingDirectory })
            {
                if (String.IsNullOrWhiteSpace(p)) continue;
                if (SamePath(path, p!, local) || IsUnder(p!, path, local)) return true;
            }
            return false;
        }

        private async Task<bool?> IsMergedAsync(IGitService git, string repoPath, string reference, string defaultBranch, CancellationToken token)
        {
            bool? local = null;
            bool? remote = null;
            try
            {
                local = await git.IsAncestorAsync(repoPath, reference, defaultBranch, token).ConfigureAwait(false);
                if (local == true) return true;
                remote = await git.IsAncestorAsync(repoPath, reference, "refs/remotes/origin/" + defaultBranch, token).ConfigureAwait(false);
                if (remote == true) return true;
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                _Logging.Debug(_Header + "could not compare " + reference + " with " + defaultBranch + " in " + repoPath + ": " + ex.Message);
                return null;
            }
            if (local == false || remote == false) return false;
            return null;
        }

        private async Task<CleanupSnapshot> LoadSnapshotAsync(Vessel vessel, CancellationToken token)
        {
            CleanupSnapshot snap = new CleanupSnapshot();
            snap.AllDocks = await _Database.Docks.EnumerateAsync(token).ConfigureAwait(false);
            snap.VesselDocks = snap.AllDocks.Where(d => d.VesselId == vessel.Id).ToList();
            snap.Missions = await _Database.Missions.EnumerateByVesselAsync(vessel.Id, token).ConfigureAwait(false);
            HashSet<string> missionIds = new HashSet<string>(snap.Missions.Select(m => m.Id), StringComparer.Ordinal);
            snap.MergeEntries = (await _Database.MergeEntries.EnumerateAsync(token).ConfigureAwait(false))
                .Where(e => e.VesselId == vessel.Id || (!String.IsNullOrEmpty(e.MissionId) && missionIds.Contains(e.MissionId!)))
                .ToList();
            snap.Captains = await _Database.Captains.EnumerateAsync(token).ConfigureAwait(false);
            return snap;
        }

        private DockHost? ResolveHost(string? harborId)
        {
            if (String.IsNullOrWhiteSpace(harborId)) return _Hosts.Local;
            if (HarborHostOverride != null) return HarborHostOverride(harborId!);
            if (_Hosts.Harbors == null || !_Hosts.Harbors.IsConnected(harborId!)) return null;
            return _Hosts.ForHarbor(harborId!);
        }

        private bool IsManagedBranch(Vessel vessel, BranchInfo branch)
        {
            if (branch == null || String.IsNullOrWhiteSpace(branch.Name)) return false;
            if (branch.IsDefault || branch.Name == vessel.DefaultBranch || _ProtectedBranchNames.Contains(branch.Name)) return false;
            return branch.Name.StartsWith(ManagedBranchPrefix, StringComparison.Ordinal);
        }

        private static bool IsLiveMergeEntry(MergeEntry e)
        {
            return e.Status == MergeStatusEnum.Queued || e.Status == MergeStatusEnum.Testing || e.Status == MergeStatusEnum.Passed;
        }

        private static void AttachMission(VesselCleanupResource r, List<Mission> missions)
        {
            Mission? m = missions.FirstOrDefault();
            if (m == null) return;
            r.MissionId = m.Id;
            r.MissionStatus = m.Status;
            r.VoyageId = m.VoyageId;
        }

        private static string MissionSuffix(VesselCleanupResource r)
        {
            return r.MissionId == null ? "" : "; mission " + r.MissionId + " is " + r.MissionStatus;
        }

        private static VesselCleanupResource Classify(VesselCleanupResource r, VesselCleanupDispositionEnum disposition, string reason)
        {
            r.Disposition = disposition;
            r.Reason = reason;
            return r;
        }

        private static VesselCleanupInventory BuildInventory(Vessel vessel, List<VesselCleanupResource> resources)
        {
            VesselCleanupInventory inventory = new VesselCleanupInventory
            {
                VesselId = vessel.Id,
                VesselName = vessel.Name,
                ScannedUtc = DateTime.UtcNow,
                Resources = resources
            };
            foreach (VesselCleanupResource r in resources)
            {
                if (r.Disposition == VesselCleanupDispositionEnum.Safe)
                {
                    inventory.SafeCount++;
                    if (r.SizeBytes.HasValue) inventory.SafeMeasuredBytes += r.SizeBytes.Value;
                    else if (r.ResourceType != VesselCleanupResourceTypeEnum.Branch) inventory.SafeUnmeasuredCount++;
                }
                else if (r.Disposition == VesselCleanupDispositionEnum.InUse) inventory.InUseCount++;
                else inventory.UnknownCount++;
            }
            return inventory;
        }

        private static int KeyOrder(string key)
        {
            if (key.StartsWith("dock:", StringComparison.Ordinal)) return 0;
            if (key.StartsWith("worktree:", StringComparison.Ordinal)) return 1;
            if (key.StartsWith("branch:", StringComparison.Ordinal)) return 2;
            return 3;
        }

        private static string? BranchFromRef(string? branchRef)
        {
            if (String.IsNullOrWhiteSpace(branchRef)) return null;
            return branchRef!.StartsWith("refs/heads/", StringComparison.Ordinal) ? branchRef.Substring("refs/heads/".Length) : branchRef;
        }

        private static string Normalize(string path, bool local)
        {
            string p = local ? PathCanonicalizer.Canonicalize(path) : path;
            return p.Replace('\\', '/').TrimEnd('/');
        }

        private static bool SamePath(string a, string b, bool local)
        {
            return String.Equals(Normalize(a, local), Normalize(b, local), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Whether child is strictly inside parent.
        /// </summary>
        private static bool IsUnder(string child, string parent, bool local)
        {
            string c = Normalize(child, local);
            string p = Normalize(parent, local);
            if (p.Length == 0) return false;
            return c.Length > p.Length + 1 && c.StartsWith(p + "/", StringComparison.OrdinalIgnoreCase);
        }

        private static string? ParentOf(string path)
        {
            string p = path.Replace('\\', '/').TrimEnd('/');
            int slash = p.LastIndexOf('/');
            return slash <= 0 ? null : p.Substring(0, slash);
        }

        private static long? MeasureDirectory(string path)
        {
            try
            {
                if (!Directory.Exists(path)) return null;
                EnumerationOptions options = new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint
                };
                long total = 0;
                foreach (FileInfo file in new DirectoryInfo(path).EnumerateFiles("*", options)) total += file.Length;
                return total;
            }
            catch (Exception)
            {
                return null;
            }
        }

        #endregion

        #region Private-Classes

        private sealed class CleanupSnapshot
        {
            public List<Dock> AllDocks { get; set; } = new List<Dock>();
            public List<Dock> VesselDocks { get; set; } = new List<Dock>();
            public List<Mission> Missions { get; set; } = new List<Mission>();
            public List<MergeEntry> MergeEntries { get; set; } = new List<MergeEntry>();
            public List<Captain> Captains { get; set; } = new List<Captain>();
        }

        private sealed class RepoState
        {
            public string? HarborId { get; set; } = null;
            public string Path { get; set; } = string.Empty;
            public DockHost? Host { get; set; } = null;
            public IReadOnlyList<GitWorktreeEntry>? Worktrees { get; set; } = null;
            public IReadOnlyList<BranchInfo>? Branches { get; set; } = null;
            public string? Error { get; set; } = null;
        }

        #endregion
    }
}
