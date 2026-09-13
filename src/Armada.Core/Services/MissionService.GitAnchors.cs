namespace Armada.Core.Services
{
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    public partial class MissionService
    {
        internal async Task<GitAnchors> ResolveDispatchGitAnchorsAsync(string path, Mission mission,
            Vessel vessel, CancellationToken token)
        {
            if (String.IsNullOrEmpty(mission.DockId))
                return await ResolveGitAnchorsAsync(path, mission, vessel, token).ConfigureAwait(false);
            try
            {
                Dock? dock = await _Database.Docks.ReadAsync(mission.DockId, token).ConfigureAwait(false);
                if (!MatchesAnchorDock(dock, path, mission, vessel)) return GitAnchors.Unresolved("dock_anchor_unavailable");
                DockGitAnchorSnapshot? seed = dock!.GitAnchorsSnapshot;
                if (seed == null || seed.MissionId != mission.Id) return GitAnchors.Unresolved("dock_anchor_unavailable");
                if (seed.State != DockGitAnchorStateEnum.Seeded) return seed.Anchors;
                DockGitAnchorSnapshot completed = await EnrichDockGitAnchorsAsync(seed, path, mission, vessel, token).ConfigureAwait(false);
                await _Database.Docks.TryCompleteGitAnchorsAsync(dock.Id, dock.CaptainId!, seed, completed, token).ConfigureAwait(false);
                // Always reload: another resolver may have won, or ownership may have changed.
                dock = await _Database.Docks.ReadAsync(mission.DockId, token).ConfigureAwait(false);
                if (!MatchesAnchorDock(dock, path, mission, vessel)
                    || dock!.GitAnchorsSnapshot?.MissionId != mission.Id
                    || dock.GitAnchorsSnapshot.State == DockGitAnchorStateEnum.Seeded)
                    return GitAnchors.Unresolved("dock_anchor_unavailable");
                return dock.GitAnchorsSnapshot.Anchors;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception)
            {
                _Logging.Warn(_Header + "dock anchor evidence unavailable for mission " + mission.Id);
                return GitAnchors.Unresolved("dock_anchor_unavailable");
            }
        }

        private static bool MatchesAnchorDock(Dock? dock, string path, Mission mission, Vessel vessel)
        {
            return dock != null && dock.Active && dock.Id == mission.DockId && dock.VesselId == vessel.Id
                && dock.TenantId == vessel.TenantId && !String.IsNullOrEmpty(mission.CaptainId)
                && dock.CaptainId == mission.CaptainId && dock.WorktreePath == path;
        }

        private async Task<DockGitAnchorSnapshot> EnrichDockGitAnchorsAsync(DockGitAnchorSnapshot seed,
            string path, Mission mission, Vessel vessel, CancellationToken token)
        {
            DockGitAnchorSnapshot result = new DockGitAnchorSnapshot
            {
                DockId = seed.DockId, MissionId = seed.MissionId, VesselId = seed.VesselId,
                ProvisionedCommit = seed.ProvisionedCommit, ProvisionedUtc = seed.ProvisionedUtc,
                ResolvedUtc = DateTime.UtcNow, State = DockGitAnchorStateEnum.Complete,
                Anchors = new GitAnchors { BaseCommit = seed.ProvisionedCommit }
            };
            try
            {
                if (_Git == null) throw new InvalidOperationException("Git unavailable.");
                string? source = await _Git.GetRevisionCommitShaAsync(path, seed.ProvisionedCommit, token).ConfigureAwait(false);
                if (!String.Equals(source, seed.ProvisionedCommit, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Provisioning commit unavailable.");
                string branch = vessel.DefaultBranch ?? "";
                if (branch.Length > 1024 || RuntimeLogFormatter.RedactSecrets(branch) != branch)
                    throw new InvalidOperationException("Target branch cannot be stored.");
                result.Anchors.TargetBranch = branch;
                if (!String.IsNullOrEmpty(branch))
                {
                    string? tip = await _Git.GetRevisionCommitShaAsync(path, branch, token).ConfigureAwait(false);
                    if (!DockGitAnchorPersistence.IsCommit(tip)) throw new InvalidOperationException("Target tip unavailable.");
                    result.Anchors.TargetTip = tip!;
                }
                string text = (mission.Title ?? "") + "\n" + (mission.Description ?? "");
                foreach (string requested in MissionSubjectExtractor.ExtractPaths(text))
                {
                    if (!DockGitAnchorPersistence.IsRelativePath(requested) || RuntimeLogFormatter.RedactSecrets(requested) != requested) { result.Truncated = true; continue; }
                    GitAnchorFileHistory history = new GitAnchorFileHistory { Path = requested };
                    string? resolved = await _Git.ResolveAnchorPathOnRevisionAsync(path, seed.ProvisionedCommit, requested, token).ConfigureAwait(false);
                    if (!String.IsNullOrEmpty(resolved))
                    {
                        if (!DockGitAnchorPersistence.IsRelativePath(resolved)
                            || RuntimeLogFormatter.RedactSecrets(resolved) != resolved) { result.Truncated = true; continue; }
                        if (resolved != requested) history.RequestedPath = requested;
                        history.Path = resolved;
                        history.ExistsOnRevision = true;
                    }
                    else history.IsExternalSourceTree = MissionSubjectExtractor.IsExternalSourceTreePath(requested);
                    history.Commits = new List<GitAnchorCommit>(await _Git.GetCommitsTouchingPathOnRevisionAsync(
                        path, seed.ProvisionedCommit, history.Path, MaxAnchorCommitsPerPath, token).ConfigureAwait(false));
                    result.Anchors.Files.Add(history);
                }
                foreach (string term in MissionSubjectExtractor.ExtractTerms(text))
                {
                    if (term.Length > 120 || RuntimeLogFormatter.RedactSecrets(term) != term)
                    { result.Truncated = true; continue; }
                    result.Anchors.PriorArt.Add(await _Git.SearchTrackedContentOnRevisionAsync(
                        path, seed.ProvisionedCommit, term, MaxAnchorSampleLocations, token).ConfigureAwait(false));
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception)
            {
                result.State = DockGitAnchorStateEnum.Incomplete;
                result.ErrorCode = "anchor_query_failed";
                result.Anchors.ResolutionError = result.ErrorCode;
            }
            if (result.Truncated)
            {
                result.State = DockGitAnchorStateEnum.Incomplete;
                result.ErrorCode ??= "anchor_data_omitted";
                result.Anchors.ResolutionError = result.ErrorCode;
            }
            // Invalid or oversized optional details are omitted together. The actual provisioning
            // commit remains available, and incomplete evidence is never presented as verified absence.
            try { DockGitAnchorPersistence.Serialize(result); }
            catch (InvalidOperationException)
            {
                result.State = DockGitAnchorStateEnum.Incomplete;
                result.Truncated = true;
                result.ErrorCode = "anchor_data_omitted";
                result.Anchors = new GitAnchors
                {
                    BaseCommit = seed.ProvisionedCommit, ResolutionError = result.ErrorCode
                };
                DockGitAnchorPersistence.Serialize(result);
            }
            return result;
        }
    }
}
