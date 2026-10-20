namespace Armada.Core.Services
{
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    public partial class MissionService
    {
        /// <summary>
        /// Returns repository paths named by the mission, excluding Armada's reserved generated
        /// briefing tree. That tree is written into the dock after fallback anchors are resolved,
        /// and it is not source content for either fallback or dock-pinned Git history.
        /// </summary>
        private static List<string> ExtractGitAnchorPaths(string missionText)
        {
            List<string> paths = new List<string>();
            foreach (string path in MissionSubjectExtractor.ExtractPaths(missionText))
            {
                // Keep the same slash and case semantics as the generated brief writer, which uses
                // these repository-relative paths verbatim. The directory separator keeps similarly named
                // source folders such as `_briefing-tools/` in the ordinary anchor flow.
                if (!path.StartsWith("_briefing/", StringComparison.Ordinal)) paths.Add(path);
            }
            return paths;
        }

        /// <summary>
        /// The most citation issues one anchors block states.
        /// </summary>
        private const int MaxCitationIssues = 8;

        /// <summary>
        /// The longest citation issue an anchors block states.
        /// </summary>
        private const int MaxCitationIssueLength = 2400;

        /// <summary>
        /// Check every line range the mission text cites against the revision the anchors describe. A
        /// path cited with lines is marked, so an absent one reads as a stale citation rather than as a
        /// file to create; a range past the end of its file, or one that no longer holds its named symbol,
        /// is stated with where the symbol is now. A citation the anchors did not resolve is resolved here
        /// the same way, and a citation into a sibling checkout is read from that checkout. A citation that
        /// cannot be resolved is stated with the reason, so silence always means the citation held.
        /// </summary>
        private async Task AddCitationChecksAsync(GitAnchors anchors, string missionText, string worktreePath,
            string revision, Vessel vessel, CancellationToken token)
        {
            List<CitedLineRange> ranges = CitedLineRangeChecker.Parse(missionText);
            if (ranges.Count == 0 || _Git == null) return;

            foreach (GitAnchorFileHistory file in anchors.Files)
            {
                string named = AnchorNamedPath(file);
                file.CitedWithLines = ranges.Exists(range => String.Equals(range.Path, named, StringComparison.Ordinal));
            }

            foreach (CitedLineRange range in ranges)
            {
                if (anchors.CitationIssues.Count >= MaxCitationIssues) break;
                string? issue = IsSiblingCitation(range.Path)
                    ? await CheckSiblingCitationAsync(range, worktreePath, vessel, token).ConfigureAwait(false)
                    : await CheckRepositoryCitationAsync(anchors, range, worktreePath, revision, token).ConfigureAwait(false);
                if (issue != null && issue.Length <= MaxCitationIssueLength && RuntimeLogFormatter.RedactSecrets(issue) == issue)
                    anchors.CitationIssues.Add(issue);
            }
        }

        /// <summary>
        /// The path a git anchor was named by in the mission text.
        /// </summary>
        private static string AnchorNamedPath(GitAnchorFileHistory file)
        {
            return String.IsNullOrEmpty(file.RequestedPath) ? file.Path : file.RequestedPath;
        }

        /// <summary>
        /// Whether a cited path leaves this checkout for a checkout beside it.
        /// </summary>
        private static bool IsSiblingCitation(string path)
        {
            return path.StartsWith("../", StringComparison.Ordinal);
        }

        /// <summary>
        /// Check one citation of a file in this checkout. An anchored path is read where the anchors resolved
        /// it, and the anchors section already states an absent or ambiguous one; any other cited path is
        /// matched against the tracked files with the same rules the anchors use.
        /// </summary>
        private async Task<string?> CheckRepositoryCitationAsync(GitAnchors anchors, CitedLineRange range,
            string worktreePath, string revision, CancellationToken token)
        {
            string trackedPath;
            GitAnchorFileHistory? file = anchors.Files.Find(candidate =>
                String.Equals(AnchorNamedPath(candidate), range.Path, StringComparison.Ordinal));
            if (file != null)
            {
                if (!file.ExistsOnRevision) return null;
                trackedPath = file.Path;
            }
            else
            {
                TrackedPathMatch? match = await TryMatchTrackedPathAsync(worktreePath, revision, range.Path, token).ConfigureAwait(false);
                if (match == null) return null;
                if (String.IsNullOrEmpty(match.Path) && match.CandidateCount > 1)
                    return "`" + range.Text + "`: `" + range.Path + "` matches " + match.CandidateCount +
                        " tracked paths on this checkout; find the one meant before relying on the cited lines.";
                if (String.IsNullOrEmpty(match.Path))
                    return "`" + range.Text + "`: `" + range.Path + "` is not tracked on this checkout, so the citation is stale; find where that code lives now.";
                if (match.IsDirectory) return null;
                trackedPath = match.Path!;
            }

            IReadOnlyList<string>? lines = await _Git!.ReadFileLinesOnRevisionAsync(worktreePath, revision, trackedPath, token).ConfigureAwait(false);
            return lines == null ? null : CitedLineRangeChecker.Evaluate(range, lines);
        }

        /// <summary>
        /// Check one citation of a file in a sibling checkout. The vessel's sibling declarations are the
        /// checkouts provisioned beside a dock, and the provisioned HEAD is the tree a captain reads, so a
        /// cited path is resolved and read there. A citation that names no declared sibling, or a sibling
        /// that is not provisioned, is stated as unchecked with the reason.
        /// </summary>
        private async Task<string?> CheckSiblingCitationAsync(CitedLineRange range, string worktreePath,
            Vessel vessel, CancellationToken token)
        {
            string cited = NormalizeCitationPath(range.Path);
            SiblingRepo? owner = null;
            string declaredPath = "";
            foreach (SiblingRepo sibling in vessel.GetSiblingRepos())
            {
                if (sibling == null) continue;
                string declared = NormalizeCitationPath(sibling.RelativePath);
                if (declared.Length == 0 || declared.Length <= declaredPath.Length) continue;
                if (!cited.StartsWith(declared + "/", StringComparison.OrdinalIgnoreCase)) continue;
                owner = sibling;
                declaredPath = declared;
            }

            if (owner == null)
                return "`" + range.Text + "` is outside this checkout and in no sibling checkout this vessel declares, so it was not checked.";

            string inner = cited.Substring(declaredPath.Length + 1);
            if (inner.Length == 0 || Array.Exists(inner.Split('/'), segment => segment.Length == 0 || segment == "." || segment == ".."))
                return "`" + range.Text + "` leaves the sibling checkout `" + declaredPath + "`, so it was not checked.";

            string siblingRoot = Path.GetFullPath(Path.Combine(worktreePath, owner.RelativePath));
            if (!Directory.Exists(siblingRoot) || !Directory.EnumerateFileSystemEntries(siblingRoot).Any())
                return "`" + range.Text + "` is in the sibling checkout `" + declaredPath + "`, which is not provisioned beside this dock, so it was not checked.";

            try
            {
                TrackedPathMatch? match = await TryMatchTrackedPathAsync(siblingRoot, "HEAD", inner, token).ConfigureAwait(false);
                if (match == null) return null;
                if (String.IsNullOrEmpty(match.Path) && match.CandidateCount > 1)
                    return "`" + range.Text + "`: `" + inner + "` matches " + match.CandidateCount + " tracked paths in the sibling checkout `" +
                        declaredPath + "`; find the one meant before relying on the cited lines.";
                if (String.IsNullOrEmpty(match.Path))
                    return "`" + range.Text + "`: `" + inner + "` is not tracked at the HEAD of the sibling checkout `" + declaredPath +
                        "`, so the citation is stale; find where that code lives now.";
                if (match.IsDirectory) return null;
                IReadOnlyList<string>? lines = await _Git!.ReadFileLinesOnRevisionAsync(siblingRoot, "HEAD", match.Path!, token).ConfigureAwait(false);
                return lines == null ? null : CitedLineRangeChecker.Evaluate(range, lines);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                // A sibling is a separate checkout: one that cannot be read leaves this checkout's evidence
                // intact and is named, rather than failing every anchor.
                return "`" + range.Text + "` is in the sibling checkout `" + declaredPath + "`, whose HEAD could not be read, so it was not checked.";
            }
        }

        /// <summary>
        /// Match a cited path against the files a revision tracks, or null when the git seam offers no
        /// revision path query.
        /// </summary>
        private async Task<TrackedPathMatch?> TryMatchTrackedPathAsync(string repositoryPath, string revision,
            string path, CancellationToken token)
        {
            try
            {
                return await _Git!.MatchAnchorPathOnRevisionAsync(repositoryPath, revision, path, token).ConfigureAwait(false);
            }
            catch (NotSupportedException)
            {
                return null;
            }
        }

        private static string NormalizeCitationPath(string? value)
        {
            return (value ?? String.Empty).Trim().Replace('\\', '/').TrimEnd('/');
        }

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
                foreach (string requested in ExtractGitAnchorPaths(text))
                {
                    if (!DockGitAnchorPersistence.IsRelativePath(requested) || RuntimeLogFormatter.RedactSecrets(requested) != requested) { result.Truncated = true; continue; }
                    GitAnchorFileHistory history = new GitAnchorFileHistory { Path = requested };
                    TrackedPathMatch match = await _Git.MatchAnchorPathOnRevisionAsync(path, seed.ProvisionedCommit, requested, token).ConfigureAwait(false);
                    string? resolved = match.Path;
                    if (String.IsNullOrEmpty(resolved) && match.CandidateCount > 1)
                    {
                        // Several tracked paths end with this name: report it as ambiguous, never as absent.
                        history.MatchingPathCount = match.CandidateCount;
                        result.Anchors.Files.Add(history);
                        continue;
                    }
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
                await AddCitationChecksAsync(result.Anchors, text, path, seed.ProvisionedCommit, vessel, token).ConfigureAwait(false);
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
