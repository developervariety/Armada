namespace Armada.Core.Services
{
    using System;
    using System.Text;

    /// <summary>
    /// The bounded block a restarted Judge receives in its description: an excerpt of its own earlier rejection
    /// (FAIL or NEEDS_REVISION), labelled as historical evidence, with the instruction to re-check every finding
    /// against the current commit and a pointer to the complete earlier output. The block is delimited by markers,
    /// so applying it again replaces the earlier block instead of adding a second one, and it never carries more
    /// than <see cref="MaxBlockChars"/> characters however large the earlier output was. The excerpt is not a
    /// verdict: the restarted Judge reaches its own.
    /// </summary>
    public static class PriorJudgeFindingsBlock
    {
        #region Public-Members

        /// <summary>
        /// Upper bound on the characters of one block, instruction text included.
        /// </summary>
        public const int MaxBlockChars = 6000;

        /// <summary>
        /// Opening marker of the block.
        /// </summary>
        public const string BeginMarker = "<!-- ARMADA:PRIOR-JUDGE-FINDINGS -->";

        /// <summary>
        /// Closing marker of the block.
        /// </summary>
        public const string EndMarker = "<!-- ARMADA:PRIOR-JUDGE-FINDINGS-END -->";

        #endregion

        #region Private-Members

        // The opening lines of the excerpt: the verdict and the summary a Judge writes first.
        private const int _HeadChars = 1200;

        private const string _ElisionMarker = "\n...(earlier output elided; read the complete earlier output)...\n";

        #endregion

        #region Public-Methods

        /// <summary>
        /// Apply the block for a Judge's earlier output to a description. A rejection output replaces any block
        /// already present, so a repeated application leaves exactly one block holding the newest excerpt. An
        /// output that is not a rejection removes a block left by an earlier run, because the newest run no longer
        /// rejects the work. Without output the description is returned unchanged. Text outside the markers,
        /// including an operator's replacement description, is kept as written.
        /// </summary>
        /// <param name="description">Mission description; may be null.</param>
        /// <param name="priorOutput">The Judge's output from the run being replaced; may be null.</param>
        /// <param name="missionId">The Judge mission id, named in the pointer to the complete output.</param>
        /// <returns>The description with at most one block.</returns>
        public static string Apply(string? description, string? priorOutput, string missionId)
        {
            string current = description ?? String.Empty;
            if (String.IsNullOrWhiteSpace(priorOutput)) return current;

            string stripped = Strip(current);
            if (!MissionService.IsJudgeRejection(priorOutput)) return stripped;
            return stripped.TrimEnd() + Build(priorOutput, missionId);
        }

        /// <summary>
        /// Remove the block, with the blank line before it, from a description. A description without a complete
        /// block is returned unchanged.
        /// </summary>
        /// <param name="description">Mission description; may be null.</param>
        /// <returns>The description without the block.</returns>
        public static string Strip(string? description)
        {
            if (String.IsNullOrEmpty(description)) return description ?? String.Empty;
            StringBuilder kept = new StringBuilder();
            int cursor = 0;
            while (true)
            {
                int begin = description.IndexOf(BeginMarker, cursor, StringComparison.Ordinal);
                if (begin < 0) break;
                int end = description.IndexOf(EndMarker, begin + BeginMarker.Length, StringComparison.Ordinal);
                if (end < 0) break;

                int blockStart = begin;
                while (blockStart > cursor && description[blockStart - 1] == '\n') blockStart--;
                kept.Append(description, cursor, blockStart - cursor);
                cursor = end + EndMarker.Length;
            }
            kept.Append(description, cursor, description.Length - cursor);
            return kept.ToString();
        }

        #endregion

        #region Private-Methods

        private static string Build(string priorOutput, string missionId)
        {
            string output = RuntimeLogFormatter.RedactSecrets(priorOutput).Replace("\r\n", "\n").Trim();
            string verdict = MissionService.DescribeJudgeVerdict(priorOutput);

            StringBuilder block = new StringBuilder();
            block.Append("\n\n").Append(BeginMarker).Append('\n');
            block.Append("## Previous Judge findings (historical; re-check, do not trust)\n");
            block.Append("This is a bounded excerpt of this review's earlier run, which ended ").Append(verdict).Append(". ");
            block.Append("It is historical evidence, not the current verdict, and it does not decide this review. ");
            block.Append("Re-check every finding below against the current commit and the current requirements, and report each as resolved, superseded, or still reproducible. ");
            block.Append("Do not carry a finding forward unverified, and do not drop one without checking it. Check the full mission scope, not only these findings.\n");
            block.Append("Complete earlier output: page the armada_mission_output MCP tool (missionId ").Append(missionId)
                .Append("); earlier runs are listed before the current run.\n\n");

            string suffix = "\n" + EndMarker;
            int room = MaxBlockChars - block.Length - suffix.Length;
            block.Append(Excerpt(output, room));
            block.Append(suffix);
            return block.ToString();
        }

        // The head holds the verdict and summary; the findings sections follow it. A report with no extractable
        // findings keeps its tail instead, where a Judge states what must change.
        private static string Excerpt(string output, int room)
        {
            if (room <= 0) return String.Empty;
            if (output.Length <= room) return output;

            int head = Math.Min(_HeadChars, room / 3);
            int headEnd = LineBoundary(output, head);
            string headText = output.Substring(0, headEnd).TrimEnd();

            int remaining = room - headText.Length - _ElisionMarker.Length;
            string rest = String.Empty;
            if (remaining > 0)
            {
                rest = StageReportEssentials.BlockingFindings(output, remaining).Replace("\r\n", "\n").Trim();
                if (rest.Length == 0 || rest.Length > remaining)
                {
                    int tailStart = output.Length - remaining;
                    if (tailStart < headEnd) tailStart = headEnd;
                    int newline = output.IndexOf('\n', tailStart);
                    if (newline >= 0 && newline < output.Length - 1) tailStart = newline + 1;
                    rest = output.Substring(tailStart).Trim();
                }
            }

            string excerpt = headText + _ElisionMarker + rest;
            if (excerpt.Length <= room) return excerpt;
            int cut = room;
            if (cut > 0 && Char.IsHighSurrogate(excerpt[cut - 1])) cut--;
            return excerpt.Substring(0, cut);
        }

        private static int LineBoundary(string text, int limit)
        {
            if (limit >= text.Length) return text.Length;
            int newline = text.LastIndexOf('\n', limit - 1, limit);
            int end = newline > 0 ? newline : limit;
            if (end > 0 && Char.IsHighSurrogate(text[end - 1])) end--;
            return end;
        }

        #endregion
    }
}
