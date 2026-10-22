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
                    ? await CheckSiblingCitationAsync(range, range.Path, worktreePath, vessel, token).ConfigureAwait(false)
                    : await CheckRepositoryCitationAsync(anchors, range, worktreePath, revision, vessel, token).ConfigureAwait(false);
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
        /// matched against the tracked files with the same rules the anchors use. A path that matches
        /// nothing as written is read the other ways text names files before it is called stale: with this
        /// repository's directory in front, with a sibling checkout's directory in front, or as the bare
        /// name of a file in a provisioned sibling checkout. A name that several tracked files match is checked
        /// against each of them, and the citation means the file whose lines hold it.
        /// </summary>
        private async Task<string?> CheckRepositoryCitationAsync(GitAnchors anchors, CitedLineRange range,
            string worktreePath, string revision, Vessel vessel, CancellationToken token)
        {
            string trackedPath;
            GitAnchorFileHistory? file = anchors.Files.Find(candidate =>
                String.Equals(AnchorNamedPath(candidate), range.Path, StringComparison.Ordinal));
            if (file != null)
            {
                if (!file.ExistsOnRevision)
                {
                    if (file.MatchingPathCount > 1)
                        return await CheckAmbiguousAnchorCitationAsync(range, worktreePath, revision, token).ConfigureAwait(false);
                    if (!file.IsExternalSourceTree) return null;
                    string? siblingPath = file.SiblingCheckout.Length > 0
                        ? file.SiblingCheckout + "/" + range.Path
                        : await SiblingPathForAsync(range.Path, worktreePath, vessel, token).ConfigureAwait(false);
                    return siblingPath == null ? null
                        : await CheckSiblingCitationAsync(range, siblingPath, worktreePath, vessel, token).ConfigureAwait(false);
                }
                trackedPath = file.Path;
            }
            else
            {
                TrackedPathMatch? match = await TryMatchTrackedPathAsync(worktreePath, revision, range.Path, token).ConfigureAwait(false);
                if (match == null) return null;
                if (String.IsNullOrEmpty(match.Path) && match.CandidateCount == 0)
                {
                    string? own = CitedPathPrefix.WithoutOwnRepository(range.Path, vessel);
                    if (own != null)
                    {
                        TrackedPathMatch? ownMatch = await TryMatchTrackedPathAsync(worktreePath, revision, own, token).ConfigureAwait(false);
                        if (ownMatch != null && ownMatch.CandidateCount > 0) match = ownMatch;
                    }
                }
                if (String.IsNullOrEmpty(match.Path) && match.CandidateCount == 0)
                {
                    string? siblingPath = await SiblingPathForAsync(range.Path, worktreePath, vessel, token).ConfigureAwait(false);
                    if (siblingPath != null)
                        return await CheckSiblingCitationAsync(range, siblingPath, worktreePath, vessel, token).ConfigureAwait(false);
                }
                if (String.IsNullOrEmpty(match.Path) && match.CandidateCount > 1)
                {
                    List<string> candidates = await _Git!.ListTrackedPathCandidatesAsync(worktreePath, revision, range.Path, token).ConfigureAwait(false);
                    if (candidates.Count == 0)
                        return "`" + range.Text + "`: `" + range.Path + "` matches " + match.CandidateCount +
                            " tracked paths on this checkout; find the one meant before relying on the cited lines.";
                    return await CheckCitationCandidatesAsync(range, range.Path, "on this checkout", candidates,
                        candidate => _Git!.ReadFileLinesOnRevisionAsync(worktreePath, revision, candidate, token)).ConfigureAwait(false);
                }
                if (String.IsNullOrEmpty(match.Path) && CitedPathPrefix.IsBareName(range.Path))
                    return "`" + range.Text + "`: `" + range.Path + "` is not tracked on this checkout or in a provisioned sibling checkout, " +
                        "so the citation is stale; find where that code lives now.";
                if (String.IsNullOrEmpty(match.Path))
                    return "`" + range.Text + "`: `" + range.Path + "` is not tracked on this checkout, so the citation is stale; find where that code lives now.";
                if (match.IsDirectory) return null;
                trackedPath = match.Path!;
            }

            IReadOnlyList<string>? lines = await _Git!.ReadFileLinesOnRevisionAsync(worktreePath, revision, trackedPath, token).ConfigureAwait(false);
            return lines == null ? null : CitedLineRangeChecker.Evaluate(range, lines);
        }

        /// <summary>
        /// Check a citation of an anchored name that several tracked files match. The anchors section already
        /// names the ambiguity, so a citation that holds in one or several of the files adds nothing; one that
        /// holds in none of them is stale and is stated with the files it was checked against.
        /// </summary>
        private async Task<string?> CheckAmbiguousAnchorCitationAsync(CitedLineRange range, string worktreePath,
            string revision, CancellationToken token)
        {
            List<string> candidates = await _Git!.ListTrackedPathCandidatesAsync(worktreePath, revision, range.Path, token).ConfigureAwait(false);
            if (candidates.Count < 2) return null;
            List<string> holding = await CitedLineRangeChecker.CandidatesHoldingAsync(new List<CitedLineRange> { range }, candidates,
                candidate => _Git!.ReadFileLinesOnRevisionAsync(worktreePath, revision, candidate, token)).ConfigureAwait(false);
            return holding.Count > 0 ? null : NoCandidateHoldsIssue(range, range.Path, "on this checkout", candidates);
        }

        /// <summary>
        /// Check a citation against every file its name matches. One file is checked as the citation names it.
        /// Among several, the citation means the file whose lines hold it: exactly one is checked silently,
        /// several are stated as ambiguous with the files that hold it, and none make the citation stale.
        /// </summary>
        private static async Task<string?> CheckCitationCandidatesAsync(CitedLineRange range, string name, string where,
            List<string> candidates, Func<string, Task<IReadOnlyList<string>?>> readLines)
        {
            if (candidates.Count == 0) return null;
            Dictionary<string, IReadOnlyList<string>?> read = new Dictionary<string, IReadOnlyList<string>?>(StringComparer.Ordinal);
            Func<string, Task<IReadOnlyList<string>?>> cached = async candidate =>
            {
                if (!read.TryGetValue(candidate, out IReadOnlyList<string>? lines))
                {
                    lines = await readLines(candidate).ConfigureAwait(false);
                    read[candidate] = lines;
                }
                return lines;
            };

            if (candidates.Count == 1)
            {
                IReadOnlyList<string>? only = await cached(candidates[0]).ConfigureAwait(false);
                return only == null ? null : CitedLineRangeChecker.Evaluate(range, only);
            }

            List<string> holding = await CitedLineRangeChecker.CandidatesHoldingAsync(new List<CitedLineRange> { range }, candidates, cached).ConfigureAwait(false);
            if (holding.Count == 1)
            {
                IReadOnlyList<string>? meant = await cached(holding[0]).ConfigureAwait(false);
                return meant == null ? null : CitedLineRangeChecker.Evaluate(range, meant);
            }
            if (holding.Count > 1)
                return "`" + range.Text + "`: `" + name + "` matches " + candidates.Count + " files " + where +
                    " and the cited lines hold in more than one (" + ListCandidates(holding) + "); find the one meant before relying on the cited lines.";
            return NoCandidateHoldsIssue(range, name, where, candidates);
        }

        /// <summary>
        /// The issue for a citation whose name matches several files when the cited lines hold in none of them.
        /// </summary>
        private static string NoCandidateHoldsIssue(CitedLineRange range, string name, string where, List<string> candidates)
        {
            return "`" + range.Text + "`: `" + name + "` matches " + candidates.Count + " files " + where +
                " and the cited lines hold in none of them (" + ListCandidates(candidates) + "), so the citation is stale; find where that code lives now.";
        }

        /// <summary>
        /// The most candidate files one citation issue names.
        /// </summary>
        private const int MaxListedCandidates = 6;

        private static string ListCandidates(List<string> candidates)
        {
            List<string> listed = candidates.Take(MaxListedCandidates).Select(candidate => "`" + candidate + "`").ToList();
            if (candidates.Count > MaxListedCandidates) listed.Add("and " + (candidates.Count - MaxListedCandidates) + " more");
            return String.Join(", ", listed);
        }

        /// <summary>
        /// The largest extraction artifact file a citation check reads.
        /// </summary>
        private const long MaxArtifactFileBytes = 4L * 1024 * 1024;

        /// <summary>
        /// The files under a sibling's extraction artifact directories in a provisioned sibling checkout that a
        /// cited name matches, as paths from the sibling root. Those trees are copied beside the tracked files
        /// and are untracked, so a name can match a tracked file and an artifact file at once, and the copy the
        /// text means may be either. A directory outside the sibling root, or one that cannot be read, is skipped.
        /// </summary>
        private static List<string> ArtifactFileCandidates(string siblingRoot, SiblingRepo sibling, string name)
        {
            List<string> files = new List<string>();
            string normalizedName = NormalizeCitationPath(name);
            int slash = normalizedName.LastIndexOf('/');
            string fileName = slash >= 0 ? normalizedName.Substring(slash + 1) : normalizedName;
            if (fileName.Length == 0 || fileName.IndexOfAny(new[] { '*', '?' }) >= 0) return files;
            string root = Path.GetFullPath(siblingRoot);
            EnumerationOptions options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                MatchCasing = MatchCasing.CaseInsensitive,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            };
            foreach (string artifact in sibling.ExtractionArtifactPaths ?? new List<string>())
            {
                string relative = NormalizeCitationPath(artifact);
                if (relative.Length == 0 || Path.IsPathRooted(relative)
                    || Array.Exists(relative.Split('/'), segment => segment.Length == 0 || segment == "." || segment == "..")) continue;
                string directory = Path.GetFullPath(Path.Combine(root, relative));
                if (!directory.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !Directory.Exists(directory)) continue;
                try
                {
                    foreach (string file in Directory.EnumerateFiles(directory, fileName, options))
                        files.Add(Path.GetRelativePath(root, file).Replace('\\', '/'));
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    continue;
                }
            }
            return TrackedPathMatcher.FileCandidates(files, normalizedName);
        }

        /// <summary>
        /// Read an extraction artifact file from a provisioned sibling checkout, or null when it is too large or
        /// cannot be read.
        /// </summary>
        private static async Task<IReadOnlyList<string>?> ReadArtifactLinesAsync(string siblingRoot, string relativePath, CancellationToken token)
        {
            try
            {
                string file = Path.Combine(Path.GetFullPath(siblingRoot), relativePath.Replace('/', Path.DirectorySeparatorChar));
                FileInfo info = new FileInfo(file);
                if (!info.Exists || info.Length > MaxArtifactFileBytes) return null;
                string content = await File.ReadAllTextAsync(file, token).ConfigureAwait(false);
                List<string> lines = new List<string>(content.Replace("\r\n", "\n").Split('\n'));
                if (lines.Count > 0 && lines[lines.Count - 1].Length == 0) lines.RemoveAt(lines.Count - 1);
                return lines;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return null;
            }
        }

        /// <summary>
        /// The <c>../</c> sibling path a cited path reaches when this checkout does not track it: a path whose
        /// first segment names a declared sibling, or a bare file name a provisioned sibling tracks. Null when
        /// neither applies.
        /// </summary>
        private async Task<string?> SiblingPathForAsync(string cited, string worktreePath, Vessel vessel, CancellationToken token)
        {
            string? prefixed = CitedPathPrefix.AsSiblingPath(cited, vessel.GetSiblingRepos());
            if (prefixed != null) return prefixed;
            if (!CitedPathPrefix.IsBareName(cited)) return null;
            return await FindBareNameInSiblingAsync(cited, worktreePath, vessel, token).ConfigureAwait(false);
        }

        /// <summary>
        /// The <c>../</c> path of a bare file name in the first provisioned sibling checkout whose HEAD tracks
        /// it or whose extraction artifact directories hold it, or null when no provisioned sibling does. A name
        /// that several of those files match keeps the bare name, so the sibling check weighs every file. A sibling that cannot be read is skipped: it is a separate checkout,
        /// and the citation check names an unreadable sibling when it reads one directly.
        /// </summary>
        private async Task<string?> FindBareNameInSiblingAsync(string name, string worktreePath, Vessel vessel, CancellationToken token)
        {
            foreach (SiblingRepo sibling in vessel.GetSiblingRepos())
            {
                if (sibling == null) continue;
                string declared = NormalizeCitationPath(sibling.RelativePath);
                if (declared.Length == 0) continue;
                string siblingRoot = Path.GetFullPath(Path.Combine(worktreePath, sibling.RelativePath));
                if (!Directory.Exists(siblingRoot) || !Directory.EnumerateFileSystemEntries(siblingRoot).Any()) continue;
                try
                {
                    TrackedPathMatch? match = await TryMatchTrackedPathAsync(siblingRoot, "HEAD", name, token).ConfigureAwait(false);
                    int tracked = match?.CandidateCount ?? 0;
                    List<string> artifacts = ArtifactFileCandidates(siblingRoot, sibling, name);
                    if (tracked == 0 && artifacts.Count == 0) continue;
                    if (tracked == 1 && artifacts.Count == 0 && !String.IsNullOrEmpty(match!.Path)) return declared + "/" + match.Path;
                    if (tracked == 0 && artifacts.Count == 1) return declared + "/" + artifacts[0];
                    return declared + "/" + name;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception)
                {
                    continue;
                }
            }
            return null;
        }

        /// <summary>
        /// Read a named path that matched nothing as written the other ways text names files. With this
        /// repository's directory in front it is anchored at the tracked path it names; with a declared
        /// sibling's directory in front, or as the bare name of a file a provisioned sibling tracks, it is a
        /// sibling path; written from a sibling checkout's own root, it is a sibling path when that checkout
        /// tracks it. Only a path none of those reach, with every declared sibling searched, stays absent.
        /// </summary>
        private async Task ApplyAnchorPathFallbacksAsync(GitAnchorFileHistory history, string requested,
            string worktreePath, string revision, Vessel vessel, string missionText, CancellationToken token)
        {
            string? own = CitedPathPrefix.WithoutOwnRepository(requested, vessel);
            if (own != null)
            {
                TrackedPathMatch? match = await TryMatchTrackedPathAsync(worktreePath, revision, own, token).ConfigureAwait(false);
                if (match != null && !String.IsNullOrEmpty(match.Path))
                {
                    history.RequestedPath = requested;
                    history.Path = match.Path!;
                    history.ExistsOnRevision = true;
                    return;
                }
                if (match != null && match.CandidateCount > 1)
                {
                    history.MatchingPathCount = match.CandidateCount;
                    return;
                }
            }
            if (await SiblingPathForAsync(requested, worktreePath, vessel, token).ConfigureAwait(false) != null)
            {
                history.IsExternalSourceTree = true;
                return;
            }
            if (!CitedPathPrefix.IsBareName(requested))
                await ApplySiblingRootFallbackAsync(history, requested, worktreePath, vessel, missionText, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Search every sibling checkout the vessel or the mission declares for a path written from that
        /// checkout's own root. A sibling whose HEAD tracks it marks the path a sibling path; a declared
        /// sibling that is not provisioned or cannot be read is recorded as unsearched, so the path is never
        /// stated as absent on a search that did not run.
        /// </summary>
        private async Task ApplySiblingRootFallbackAsync(GitAnchorFileHistory history, string requested,
            string worktreePath, Vessel vessel, string missionText, CancellationToken token)
        {
            List<string> unverified = new List<string>();
            foreach (string root in DeclaredSiblingRoots(vessel, missionText))
            {
                string siblingRoot = Path.GetFullPath(Path.Combine(worktreePath, root));
                if (!Directory.Exists(siblingRoot) || !Directory.EnumerateFileSystemEntries(siblingRoot).Any())
                {
                    unverified.Add(root);
                    continue;
                }
                try
                {
                    TrackedPathMatch? match = await TryMatchTrackedPathAsync(siblingRoot, "HEAD", requested, token).ConfigureAwait(false);
                    if (match == null)
                    {
                        unverified.Add(root);
                        continue;
                    }
                    if (String.IsNullOrEmpty(match.Path) && match.CandidateCount == 0) continue;
                    history.IsExternalSourceTree = true;
                    history.SiblingCheckout = root;
                    history.UnverifiedSiblingCheckouts = new List<string>();
                    return;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception)
                {
                    // A sibling is a separate checkout: one that cannot be read leaves the path unverified.
                    unverified.Add(root);
                }
            }
            history.UnverifiedSiblingCheckouts = unverified;
        }

        /// <summary>
        /// The sibling checkouts a dock may hold for this mission, as <c>../</c> paths: every sibling the
        /// vessel declares, then every <c>../name</c> root the mission text names a file under.
        /// </summary>
        private static List<string> DeclaredSiblingRoots(Vessel vessel, string missionText)
        {
            List<string> roots = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (SiblingRepo sibling in vessel.GetSiblingRepos())
            {
                if (sibling == null) continue;
                string declared = NormalizeCitationPath(sibling.RelativePath);
                if (DockGitAnchorPersistence.IsSiblingRoot(declared) && seen.Add(declared)) roots.Add(declared);
            }
            foreach (string path in ContextPackManifest.ExtractSiblingPaths(missionText))
            {
                string[] segments = NormalizeCitationPath(path).Split('/');
                if (segments.Length < 3 || segments[0] != ".." || segments[1].Length == 0
                    || segments[1] == "." || segments[1] == "..") continue;
                string root = "../" + segments[1];
                if (seen.Add(root)) roots.Add(root);
            }
            return roots;
        }

        /// <summary>
        /// Check one citation of a file in a sibling checkout. The vessel's sibling declarations are the
        /// checkouts provisioned beside a dock, and the provisioned HEAD is the tree a captain reads, so a
        /// cited path is resolved and read there, together with the files the sibling's extraction artifact
        /// directories hold there. A name that several of those files match is checked against each of them,
        /// and the citation means the file whose lines hold it. A citation that names no declared sibling, or a
        /// sibling that is not provisioned, is stated as unchecked with the reason.
        /// </summary>
        private async Task<string?> CheckSiblingCitationAsync(CitedLineRange range, string citedPath, string worktreePath,
            Vessel vessel, CancellationToken token)
        {
            string cited = NormalizeCitationPath(citedPath);
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
                if (match.IsDirectory) return null;
                List<string> tracked = !String.IsNullOrEmpty(match.Path) ? new List<string> { match.Path! }
                    : match.CandidateCount > 1
                        ? await _Git!.ListTrackedPathCandidatesAsync(siblingRoot, "HEAD", inner, token).ConfigureAwait(false)
                        : new List<string>();
                List<string> artifacts = ArtifactFileCandidates(siblingRoot, owner, inner).Where(artifact => !tracked.Contains(artifact)).ToList();
                if (tracked.Count + artifacts.Count == 0)
                {
                    if (match.CandidateCount > 1)
                        return "`" + range.Text + "`: `" + inner + "` matches " + match.CandidateCount + " tracked paths in the sibling checkout `" +
                            declaredPath + "`; find the one meant before relying on the cited lines.";
                    return "`" + range.Text + "`: `" + inner + "` is not tracked at the HEAD of the sibling checkout `" + declaredPath +
                        "`, so the citation is stale; find where that code lives now.";
                }
                List<string> candidates = tracked.Concat(artifacts).ToList();
                return await CheckCitationCandidatesAsync(range, inner, "in the sibling checkout `" + declaredPath + "`", candidates,
                    candidate => artifacts.Contains(candidate)
                        ? ReadArtifactLinesAsync(siblingRoot, candidate, token)
                        : _Git!.ReadFileLinesOnRevisionAsync(siblingRoot, "HEAD", candidate, token)).ConfigureAwait(false);
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
                    else
                    {
                        history.IsExternalSourceTree = MissionSubjectExtractor.IsExternalSourceTreePath(requested);
                        if (!history.IsExternalSourceTree)
                            await ApplyAnchorPathFallbacksAsync(history, requested, path, seed.ProvisionedCommit, vessel, text, token).ConfigureAwait(false);
                        if (history.ExistsOnRevision && (!DockGitAnchorPersistence.IsRelativePath(history.Path)
                            || RuntimeLogFormatter.RedactSecrets(history.Path) != history.Path)) { result.Truncated = true; continue; }
                        if (history.MatchingPathCount > 1)
                        {
                            result.Anchors.Files.Add(history);
                            continue;
                        }
                    }
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
