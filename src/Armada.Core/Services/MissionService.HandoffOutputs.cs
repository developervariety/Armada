namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.RegularExpressions;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Context;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    public partial class MissionService
    {
        #region Public-Members

        /// <summary>
        /// Dock folder that holds the complete output of every earlier stage a brief cites. A handoff embeds a
        /// bounded preview of a long report; the whole report sits here, so a downstream stage reads every
        /// finding without paging an MCP tool.
        /// </summary>
        internal const string HandoffOutputFolder = "_briefing/handoff";

        #endregion

        #region Private-Members

        // A brief cites an earlier stage's output by its artifact reference.
        private static readonly Regex _MissionOutputArtifactRef = new Regex(
            @"mission-output:(msn_[A-Za-z0-9_-]+)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // Bounds the files one brief stages: a rescue of a rescue cites a handful of stages, never dozens.
        private const int _MaxHandoffOutputFiles = 16;

        /// <summary>
        /// Text a Codex brief adds: the Codex command runner refuses <c>rm -f</c> and its exec isolate has no
        /// digest API, so the brief names the commands that work.
        /// </summary>
        internal const string CodexShellSection =
            "## Codex Shell\n\n" +
            "Remove temporary files with `rm <path>` (no `-f`) or `find <dir> -name '<pattern>' -delete`; " +
            "compute digests with `sha256sum <file>`.\n";

        #endregion

        #region Private-Methods

        /// <summary>
        /// The dock path of one earlier stage's complete output.
        /// </summary>
        /// <param name="missionId">The earlier stage's mission id.</param>
        /// <returns>The dock-relative path, using forward slashes.</returns>
        internal static string HandoffOutputPath(string missionId)
        {
            return HandoffOutputFolder + "/" + missionId + ".md";
        }

        /// <summary>
        /// The mission ids whose output artifact a description cites, in first-seen order, without duplicates,
        /// at most <see cref="_MaxHandoffOutputFiles"/>.
        /// </summary>
        /// <param name="description">Mission description.</param>
        /// <returns>The cited mission ids.</returns>
        internal static List<string> FindCitedOutputMissionIds(string? description)
        {
            List<string> ids = new List<string>();
            if (String.IsNullOrEmpty(description)) return ids;
            foreach (Match match in _MissionOutputArtifactRef.Matches(description))
            {
                string id = match.Groups[1].Value;
                if (ids.Contains(id, StringComparer.Ordinal)) continue;
                ids.Add(id);
                if (ids.Count >= _MaxHandoffOutputFiles) break;
            }
            return ids;
        }

        /// <summary>
        /// The complete redacted output of every earlier stage the description cites, one file each. The file
        /// holds exactly the artifact content, so its UTF-8 SHA-256 equals the digest the brief states. A cited
        /// mission that is absent, is the mission itself, belongs to another tenant or owner, or has no output
        /// produces no file.
        /// </summary>
        /// <param name="mission">The mission whose brief is generated.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The files to write under <see cref="HandoffOutputFolder"/>.</returns>
        private async Task<List<ContextBriefFile>> BuildHandoffOutputFilesAsync(Mission mission, CancellationToken token)
        {
            List<ContextBriefFile> files = new List<ContextBriefFile>();
            foreach (string id in FindCitedOutputMissionIds(mission.Description))
            {
                if (String.Equals(id, mission.Id, StringComparison.Ordinal)) continue;
                Mission? cited = await _Database.Missions.ReadAsync(id, token).ConfigureAwait(false);
                if (cited == null || cited.AgentOutput == null) continue;
                if (!String.Equals(cited.TenantId ?? Constants.DefaultTenantId, mission.TenantId ?? Constants.DefaultTenantId, StringComparison.Ordinal)
                    || !String.Equals(cited.UserId ?? Constants.DefaultUserId, mission.UserId ?? Constants.DefaultUserId, StringComparison.Ordinal))
                    continue;
                files.Add(new ContextBriefFile
                {
                    RelativePath = HandoffOutputPath(cited.Id),
                    Content = RuntimeLogFormatter.RedactSecrets(cited.AgentOutput)
                });
            }
            return files;
        }

        #endregion
    }
}
