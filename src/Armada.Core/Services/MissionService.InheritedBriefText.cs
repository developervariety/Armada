namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Text;
    using System.Text.Json;
    using System.Text.RegularExpressions;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Context;
    using Armada.Core.Models;

    public partial class MissionService
    {
        #region Public-Members

        /// <summary>
        /// Event type recorded when a stage's dock starts at a commit that does not contain the commit its
        /// predecessor stage recorded. The stage still runs; the event and one brief line make the gap visible.
        /// </summary>
        internal const string StartBehindPredecessorEventType = "mission.start_behind_predecessor";

        #endregion

        #region Private-Members

        private static readonly Regex _GitAnchorsHeadingPattern = new Regex(
            @"^\s{0,3}#{1,2}\s+Git Anchors\s*$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex _TopHeadingPattern = new Regex(
            @"^\s{0,3}#{1,2}\s",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex _SubHeadingPattern = new Regex(
            @"^\s{0,3}#{3,6}\s+(?<title>.*?)\s*$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>
        /// The sub-headings the Git Anchors section renders. Any other sub-heading after a copied block starts
        /// content that is not part of it, such as a handoff's agent output.
        /// </summary>
        private static readonly HashSet<string> _GitAnchorsSubHeadings = new HashSet<string>(StringComparer.Ordinal)
        {
            "Recent history of the paths this mission names",
            "Cited line ranges to recheck",
            "Tracked content for this mission's subject terms",
            "Prior art for this mission's subject terms"
        };

        #endregion

        #region Private-Methods

        /// <summary>
        /// Remove every <c>## Git Anchors</c> block the text carries. Anchors describe the checkout of the brief
        /// they were rendered for; a copy inherited from an earlier stage's brief or report names another
        /// checkout's commits and paths, and the brief adds its own block for this dock. A block ends at the next
        /// top-level heading, a sub-heading the anchors section does not render, or a code fence.
        /// </summary>
        /// <param name="text">Mission description, or null.</param>
        /// <returns>The text without copied anchor blocks.</returns>
        internal static string? StripInheritedGitAnchorsBlocks(string? text)
        {
            if (String.IsNullOrEmpty(text) || text.IndexOf("Git Anchors", StringComparison.Ordinal) < 0) return text;

            string[] lines = text.Split('\n');
            StringBuilder sb = new StringBuilder(text.Length);
            bool skipping = false;
            bool removed = false;
            bool first = true;
            foreach (string rawLine in lines)
            {
                string line = rawLine.TrimEnd('\r');
                if (skipping)
                {
                    string trimmed = line.TrimStart();
                    bool fence = trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal);
                    Match sub = _SubHeadingPattern.Match(line);
                    bool ends = fence
                        || _TopHeadingPattern.IsMatch(line)
                        || (sub.Success && !_GitAnchorsSubHeadings.Contains(sub.Groups["title"].Value));
                    if (!ends) continue;
                    skipping = false;
                }

                if (_GitAnchorsHeadingPattern.IsMatch(line))
                {
                    skipping = true;
                    removed = true;
                    continue;
                }

                if (!first) sb.Append('\n');
                sb.Append(rawLine);
                first = false;
            }

            return removed ? sb.ToString() : text;
        }

        /// <summary>
        /// The Armada-only instruction filenames the text names that this dock does not hold and that are not
        /// this runtime's own file. These are the names <see cref="RetargetInstructionFileNames(string?, string, string)"/>
        /// replaces.
        /// </summary>
        /// <param name="text">Text to scan, or null.</param>
        /// <param name="instructionsFileName">The instruction filename this captain's runtime receives.</param>
        /// <param name="worktreePath">Dock worktree path.</param>
        /// <returns>The foreign filenames, in a fixed order; empty when none.</returns>
        internal static List<string> FindForeignInstructionFileNames(string? text, string instructionsFileName, string worktreePath, ISet<string>? existingInstructions = null)
        {
            List<string> found = new List<string>();
            if (String.IsNullOrEmpty(text) || String.IsNullOrEmpty(instructionsFileName)) return found;

            foreach (string foreign in _ArmadaOnlyInstructionFileNames)
            {
                if (String.Equals(foreign, instructionsFileName, StringComparison.Ordinal)) continue;
                if (text.IndexOf(foreign, StringComparison.Ordinal) < 0) continue;
                if (existingInstructions != null ? existingInstructions.Contains(foreign)
                    : !String.IsNullOrEmpty(worktreePath) && File.Exists(Path.Combine(worktreePath, foreign))) continue;
                if (!Regex.IsMatch(text, @"(?<![\w./-])" + Regex.Escape(foreign) + @"(?![\w-])", RegexOptions.CultureInvariant)) continue;
                found.Add(foreign);
            }

            return found;
        }

        /// <summary>
        /// The section that names this runtime's instruction file for staged earlier-stage reports that name
        /// another runtime's file. A staged report keeps its exact bytes, because the brief states its digest,
        /// so the section states the replacement instead of rewriting the file.
        /// </summary>
        /// <param name="stagedFiles">Staged earlier-stage report files.</param>
        /// <param name="instructionsFileName">The instruction filename this captain's runtime receives.</param>
        /// <param name="worktreePath">Dock worktree path.</param>
        /// <returns>The section, or an empty string when no staged file names a foreign instruction file.</returns>
        internal static string BuildStagedInstructionFileNamesSection(List<ContextBriefFile>? stagedFiles, string instructionsFileName, string worktreePath, ISet<string>? existingInstructions = null)
        {
            if (stagedFiles == null || stagedFiles.Count == 0) return String.Empty;

            List<string> foreign = new List<string>();
            List<string> paths = new List<string>();
            foreach (ContextBriefFile file in stagedFiles)
            {
                List<string> names = FindForeignInstructionFileNames(file.Content, instructionsFileName, worktreePath, existingInstructions);
                if (names.Count == 0) continue;
                paths.Add(file.RelativePath);
                foreach (string name in names)
                    if (!foreign.Contains(name)) foreign.Add(name);
            }
            if (foreign.Count == 0) return String.Empty;

            StringBuilder sb = new StringBuilder();
            sb.Append("## Instruction File Names in Staged Reports\n");
            sb.Append("These staged earlier-stage reports name ");
            sb.Append(String.Join(", ", foreign.ConvertAll(n => "`" + n + "`")));
            sb.Append(", which this dock does not hold. Read each as `").Append(instructionsFileName).Append("`, your instruction file:\n");
            foreach (string path in paths) sb.Append("- `").Append(path).Append("`\n");
            return sb.ToString();
        }

        /// <summary>
        /// When the stage's checkout does not contain the commit its same-vessel predecessor recorded, record a
        /// <see cref="StartBehindPredecessorEventType"/> event and return one brief line that names both commits.
        /// The stage is never held or skipped: a stage cut from a fresh branch can be correct, and an ancestry
        /// probe that cannot answer is not evidence of a gap, so it produces nothing.
        /// </summary>
        /// <param name="worktreePath">Dock worktree path.</param>
        /// <param name="mission">Mission whose brief is generated.</param>
        /// <param name="captain">Captain assigned, or null.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The brief line, or an empty string.</returns>
        private async Task<string> ReportStartBehindPredecessorAsync(string worktreePath, Mission mission, Captain? captain, CancellationToken token)
        {
            if (_Git == null || String.IsNullOrEmpty(worktreePath) || String.IsNullOrEmpty(mission.DependsOnMissionId)) return String.Empty;

            try
            {
                Mission? predecessor = await _Database.Missions.ReadAsync(mission.DependsOnMissionId, token).ConfigureAwait(false);
                if (predecessor == null || String.IsNullOrWhiteSpace(predecessor.CommitHash)) return String.Empty;
                if (!String.IsNullOrEmpty(predecessor.VesselId) && !String.IsNullOrEmpty(mission.VesselId)
                    && !String.Equals(predecessor.VesselId, mission.VesselId, StringComparison.Ordinal))
                    return String.Empty;

                string predecessorCommit = predecessor.CommitHash!.Trim();
                string? startCommit = await _Git.GetHeadCommitHashAsync(worktreePath, token).ConfigureAwait(false);
                if (String.IsNullOrWhiteSpace(startCommit) || IsSameCommit(startCommit!.Trim(), predecessorCommit)) return String.Empty;

                bool? contains = await _Git.TryIsAncestorAsync(worktreePath, predecessorCommit, "HEAD", token).ConfigureAwait(false);
                if (contains != false) return String.Empty;

                string start = startCommit.Trim();
                _Logging.Warn(_Header + "mission " + mission.Id + " starts at " + start + ", which does not contain " +
                    predecessorCommit + " recorded by predecessor " + predecessor.Id);

                ArmadaEvent evt = new ArmadaEvent(
                    StartBehindPredecessorEventType,
                    "Stage starts at " + start + ", which does not contain predecessor commit " + predecessorCommit);
                EventOwnerScope.ApplyFromMission(evt, mission);
                evt.EntityType = "mission";
                evt.EntityId = mission.Id;
                evt.CaptainId = captain?.Id;
                evt.MissionId = mission.Id;
                evt.VesselId = mission.VesselId;
                evt.VoyageId = mission.VoyageId;
                evt.Payload = JsonSerializer.Serialize(new
                {
                    MissionId = mission.Id,
                    PredecessorMissionId = predecessor.Id,
                    StartCommit = start,
                    PredecessorCommit = predecessorCommit
                });
                await _Database.Events.CreateAsync(evt, token).ConfigureAwait(false);

                return "This checkout starts at `" + start + "`, which does not contain `" + predecessorCommit +
                    "`, the commit predecessor stage `" + predecessor.Id + "` recorded; do not assume its work is here, and report the gap if you need it.\n";
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "could not compare the start commit of mission " + mission.Id + " with its predecessor: " + ex.Message);
                return String.Empty;
            }
        }

        #endregion
    }
}
