namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Text.RegularExpressions;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Context;
    using Armada.Core.Models;

    public partial class MissionService
    {
        #region Public-Members

        /// <summary>
        /// Dock folder that holds the complete handoff text of a mission whose persisted description was cut to
        /// the handoff budget. The cut marker in the description names the first file of this folder.
        /// </summary>
        internal const string HandoffFullTextFolder = "_briefing/handoff-full";

        /// <summary>
        /// First file of the complete handoff text in the dock. The cut marker names it, so it is fixed whatever
        /// the number of files the text needs.
        /// </summary>
        internal const string HandoffFullTextFirstFile = HandoffFullTextFolder + "/01-handoff.md";

        /// <summary>
        /// Folder under the log directory that keeps the complete handoff text of each cut description, one
        /// <c>&lt;mission id&gt;.md</c> file each. The instruction-snapshot retention bounds it.
        /// </summary>
        public const string HandoffFullTextDirectoryName = "handoffs";

        #endregion

        #region Private-Members

        private const string _HandoffFullTextHeading = "Full handoff text";

        private const string _InlineTextPhrase = "the inline text of this brief";

        private static readonly Regex _ReadInstructionPattern = new Regex(
            @"\b(?:read|reads|reading|open|opens|cat|see)\b",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        #endregion

        #region Public-Methods

        /// <summary>
        /// The server path that keeps one mission's complete handoff text.
        /// </summary>
        /// <param name="logDirectory">Admiral log directory.</param>
        /// <param name="missionId">Mission id.</param>
        /// <returns>The absolute path.</returns>
        public static string FullHandoffStorePath(string logDirectory, string missionId)
        {
            return Path.Combine(logDirectory, HandoffFullTextDirectoryName, missionId + ".md");
        }

        #endregion

        #region Private-Methods

        /// <summary>
        /// Keep the complete handoff text of a description that is about to be cut. Returns false, with a
        /// warning, when the text cannot be written; the caller then names no dock file in its cut marker.
        /// </summary>
        private async Task<bool> StoreFullHandoffAsync(string missionId, string text, CancellationToken token)
        {
            string path = FullHandoffStorePath(_Settings.LogDirectory, missionId);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(path, text, token).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                _Logging.Warn(_Header + "pipeline handoff: could not keep the full handoff text of mission " + missionId
                    + " at " + path + "; the cut description names only the branch: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Remove a kept handoff text that a later handoff for the same mission no longer cuts, so the dock never
        /// receives a stale copy.
        /// </summary>
        private void DeleteStoredFullHandoff(string missionId)
        {
            string path = FullHandoffStorePath(_Settings.LogDirectory, missionId);
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                _Logging.Warn(_Header + "pipeline handoff: could not remove the stale full handoff text " + path + ": " + ex.Message);
            }
        }

        /// <summary>
        /// The complete handoff text as bounded dock files, when the description carries the cut marker that
        /// names them and the kept text exists. Empty otherwise.
        /// </summary>
        /// <param name="mission">The mission whose brief is generated.</param>
        /// <param name="transform">The path rewrites the brief applies to the description, applied to the text too.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The files to write under <see cref="HandoffFullTextFolder"/>.</returns>
        private async Task<List<ContextBriefFile>> BuildFullHandoffFilesAsync(Mission mission, Func<string, string> transform, CancellationToken token)
        {
            List<ContextBriefFile> files = new List<ContextBriefFile>();
            if (String.IsNullOrEmpty(mission.Description)
                || mission.Description.IndexOf(HandoffFullTextFirstFile, StringComparison.Ordinal) < 0)
                return files;

            string path = FullHandoffStorePath(_Settings.LogDirectory, mission.Id);
            if (!File.Exists(path))
            {
                _Logging.Warn(_Header + "mission " + mission.Id + " names " + HandoffFullTextFirstFile
                    + " but the kept handoff text " + path + " is absent; the brief points its reads at the inline text");
                return files;
            }

            string text = await File.ReadAllTextAsync(path, token).ConfigureAwait(false);
            return BriefFilePacker.Pack(HandoffFullTextFolder, "handoff", _HandoffFullTextHeading, transform(text));
        }

        /// <summary>
        /// Rewrite each line that tells the captain to read a <c>_briefing/</c> file the dock will not hold. The
        /// path is replaced by <paramref name="replacementPath"/> when one is staged, and otherwise by a pointer
        /// to the inline text, so a brief never sends a captain to read a file that does not exist. A path under
        /// <see cref="MissionDescriptionFolder"/> is judged against <paramref name="descriptionFiles"/>, because
        /// that folder is rewritten after this check; every other path is judged against the dock.
        /// </summary>
        /// <param name="text">Mission description, or null.</param>
        /// <param name="worktreePath">Dock worktree path.</param>
        /// <param name="descriptionFiles">The description files this brief writes.</param>
        /// <param name="replacementPath">A staged dock file to point the reads at, or null for the inline text.</param>
        /// <returns>The text with each unreadable read target replaced.</returns>
        internal static string? RewriteUnstagedBriefingReads(string? text, string worktreePath, List<ContextBriefFile> descriptionFiles, string? replacementPath)
        {
            if (String.IsNullOrEmpty(text) || String.IsNullOrEmpty(worktreePath)) return text;
            if (text.IndexOf("_briefing/", StringComparison.Ordinal) < 0) return text;

            HashSet<string> planned = new HashSet<string>(StringComparer.Ordinal);
            if (descriptionFiles != null)
                foreach (ContextBriefFile file in descriptionFiles) planned.Add(file.RelativePath);

            bool Absent(string path)
            {
                if (path == MissionDescriptionFolder || path.StartsWith(MissionDescriptionFolder + "/", StringComparison.Ordinal))
                    return path == MissionDescriptionFolder ? planned.Count == 0 : !planned.Contains(path);
                string full = Path.Combine(worktreePath, path.Replace('/', Path.DirectorySeparatorChar));
                return !File.Exists(full) && !Directory.Exists(full);
            }

            string replacement = String.IsNullOrEmpty(replacementPath) ? _InlineTextPhrase : "`" + replacementPath + "`";
            string[] lines = text.Split('\n');
            bool changed = false;
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line.IndexOf("_briefing/", StringComparison.Ordinal) < 0 || !_ReadInstructionPattern.IsMatch(line)) continue;

                bool lineChanged = false;
                string rewritten = _BriefingPathPattern.Replace(line, match =>
                {
                    if (!Absent(match.Value)) return match.Value;
                    lineChanged = true;
                    return "\u0001";
                });
                if (!lineChanged) continue;

                // A backticked path is replaced with its backticks, and a range of replaced paths ("01 .. 03",
                // "01 to 03", a list) collapses to one pointer.
                rewritten = rewritten.Replace("`\u0001`", "\u0001");
                rewritten = Regex.Replace(rewritten, "\u0001(?:\\s*(?:\\.\\.|to|through|,|and|-)\\s*\u0001)+", "\u0001", RegexOptions.CultureInvariant);
                lines[i] = rewritten.Replace("\u0001", replacement);
                changed = true;
            }

            return changed ? String.Join("\n", lines) : text;
        }

        #endregion
    }
}
