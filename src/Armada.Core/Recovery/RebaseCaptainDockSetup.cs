namespace Armada.Core.Recovery
{
    using System;
    using System.Collections.Generic;
    using System.Text;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using SyslogLogging;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;

    /// <summary>
    /// Builds a <see cref="RebaseCaptainMissionSpec"/> describing a rebase-captain
    /// recovery mission. Pure with respect to the database (reads only): assembles
    /// the brief from the failed mission description plus a conflict-context
    /// appendix, synthesizes the prestaged conflict-state marker, and pins the
    /// landing branch to the failed mission's captain branch so resolution
    /// commits land on the same branch the merge queue is watching.
    /// </summary>
    public sealed class RebaseCaptainDockSetup : IRebaseCaptainDockSetup
    {
        #region Private-Members

        /// <summary>Header used for log lines.</summary>
        private const string _Header = "[RebaseCaptainDockSetup] ";

        /// <summary>High-tier rebase model per the auto-recovery design spec.</summary>
        public const string PreferredModelClaudeOpus47 = "claude-opus-4-7";

        /// <summary>Playbook id for the inline rebase-captain playbook.</summary>
        public const string RebaseCaptainPlaybookId = "pbk_rebase_captain";

        /// <summary>Conflict-state marker file written into the rebase dock briefing dir.</summary>
        public const string ConflictStateMarkerRelativePath = "_briefing/conflict-state.md";

        /// <summary>Original briefing path looked up on the failed mission's prestaged files.</summary>
        public const string OriginalBriefingPath = "_briefing/spec.md";

        /// <summary>Maximum number of trailing lines from git/test output kept on the brief.</summary>
        private const int _OutputTailLineCount = 80;

        private readonly IGitService _Git;
        private readonly DatabaseDriver _Database;
        private readonly LoggingModule _Logging;

        private static readonly JsonSerializerOptions _PrettyJson = new JsonSerializerOptions
        {
            WriteIndented = true
        };

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="git">Git service for reading conflict-state metadata.</param>
        /// <param name="database">Database driver (currently unused at the read level
        /// but kept on the seam for future enrichment of the brief from related rows).</param>
        /// <param name="logging">Logging module.</param>
        public RebaseCaptainDockSetup(IGitService git, DatabaseDriver database, LoggingModule logging)
        {
            _Git = git ?? throw new ArgumentNullException(nameof(git));
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
        }

        #endregion

        #region Public-Methods

        /// <inheritdoc />
        public async Task<RebaseCaptainMissionSpec> BuildAsync(
            MergeEntry failedEntry,
            Mission failedMission,
            MergeFailureClassification classification,
            CancellationToken token = default)
        {
            if (failedEntry == null) throw new ArgumentNullException(nameof(failedEntry));
            if (failedMission == null) throw new ArgumentNullException(nameof(failedMission));
            if (classification == null) throw new ArgumentNullException(nameof(classification));

            string captainBranch = ResolveCaptainBranch(failedEntry, failedMission);
            string brief = BuildBrief(failedEntry, failedMission, classification);
            List<PrestagedFile> prestaged = await BuildPrestagedFilesAsync(failedMission, classification, captainBranch, token).ConfigureAwait(false);

            List<SelectedPlaybook> selected = new List<SelectedPlaybook>
            {
                new SelectedPlaybook
                {
                    PlaybookId = RebaseCaptainPlaybookId,
                    DeliveryMode = PlaybookDeliveryModeEnum.InlineFullContent
                }
            };

            _Logging.Info(_Header + "built rebase-captain spec mission=" + failedMission.Id + " entry=" + failedEntry.Id + " captainBranch=" + captainBranch);

            return new RebaseCaptainMissionSpec(
                Brief: brief,
                PrestagedFiles: prestaged,
                PreferredModel: PreferredModelClaudeOpus47,
                LandingTargetBranch: captainBranch,
                SelectedPlaybooks: selected,
                DependsOnMissionId: null,
                RecoveryAttempts: 0);
        }

        #endregion

        #region Private-Methods

        private static string ResolveCaptainBranch(MergeEntry failedEntry, Mission failedMission)
        {
            // Mission.BranchName is the canonical captain-branch reference; merge-queue
            // entry's BranchName mirrors it. Fall back to the entry when the mission
            // record is missing the field (older rows).
            if (!String.IsNullOrEmpty(failedMission.BranchName)) return failedMission.BranchName;
            return failedEntry.BranchName;
        }

        private static string BuildBrief(MergeEntry failedEntry, Mission failedMission, MergeFailureClassification classification)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(failedMission.Description ?? string.Empty);
            sb.Append("\n\n## Conflict context appendix\n\n");
            sb.Append("Failure class: ").Append(classification.FailureClass).Append('\n');
            sb.Append("Summary: ").Append(classification.Summary ?? string.Empty).Append('\n');
            sb.Append("Diff line count: ").Append(failedEntry.DiffLineCount).Append('\n');
            sb.Append("Captain branch: ").Append(failedMission.BranchName ?? failedEntry.BranchName).Append('\n');
            sb.Append("Target branch: ").Append(failedEntry.TargetBranch).Append('\n');

            sb.Append("\n### Conflicted files\n\n");
            string conflictedJson = JsonSerializer.Serialize(classification.ConflictedFiles ?? Array.Empty<string>(), _PrettyJson);
            sb.Append("```json\n").Append(conflictedJson).Append("\n```\n");

            string testOutputTail = ExtractTail(failedEntry.TestOutput, _OutputTailLineCount);
            if (!String.IsNullOrEmpty(testOutputTail))
            {
                sb.Append("\n### Last ").Append(_OutputTailLineCount).Append(" lines of git/test output\n\n");
                sb.Append("```\n").Append(testOutputTail).Append("\n```\n");
            }

            return sb.ToString();
        }

        private async Task<List<PrestagedFile>> BuildPrestagedFilesAsync(
            Mission failedMission,
            MergeFailureClassification classification,
            string captainBranch,
            CancellationToken token)
        {
            List<PrestagedFile> prestaged = new List<PrestagedFile>();

            // Carry forward the original spec briefing if the failed mission had one.
            if (failedMission.PrestagedFiles != null)
            {
                foreach (PrestagedFile original in failedMission.PrestagedFiles)
                {
                    if (String.Equals(original.DestPath, OriginalBriefingPath, StringComparison.Ordinal))
                    {
                        prestaged.Add(new PrestagedFile(original.SourcePath, original.DestPath));
                    }
                }
            }

            // Synthesize a conflict-state.md describing what's currently checked out on
            // the captain branch (no source path -- this is conceptual prestaging that
            // the dock provisioner materializes; tests assert the marker is present).
            string syntheticPath = "<conflict-state-synthesized:" + failedMission.Id + ":" + captainBranch + ">";
            prestaged.Add(new PrestagedFile(syntheticPath, ConflictStateMarkerRelativePath));

            await Task.CompletedTask;
            return prestaged;
        }

        private static string ExtractTail(string? text, int lineCount)
        {
            if (String.IsNullOrEmpty(text)) return string.Empty;
            string[] lines = text.Replace("\r\n", "\n").Split('\n');
            if (lines.Length <= lineCount) return string.Join('\n', lines);
            string[] tail = new string[lineCount];
            Array.Copy(lines, lines.Length - lineCount, tail, 0, lineCount);
            return string.Join('\n', tail);
        }

        #endregion
    }
}
