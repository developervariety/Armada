namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Text.RegularExpressions;
    using Armada.Core.Models;

    /// <summary>
    /// The one grammar for mission blocks in an Architect's output, read by the pipeline handoff and by
    /// the operator's Architect parse tool alike. A block opens with <c>[ARMADA:MISSION]</c> at the start
    /// of a line and ends at <c>[ARMADA:MISSION-END]</c> (or the older <c>[/ARMADA:MISSION]</c>), at the
    /// next opening marker, or at the end of the text. A marker inside a sentence is prose, not a block.
    /// </summary>
    public static class ArchitectMissionBlocks
    {
        #region Public-Members

        /// <summary>Opening marker of a mission block.</summary>
        public const string OpenMarker = "[ARMADA:MISSION]";

        /// <summary>Closing marker of a mission block.</summary>
        public const string EndMarker = "[ARMADA:MISSION-END]";

        /// <summary>Older closing marker, still accepted.</summary>
        public const string LegacyEndMarker = "[/ARMADA:MISSION]";

        #endregion

        #region Private-Members

        private static readonly Regex _OpenAtLineStart = new Regex(@"(?m)^[ \t]*\[ARMADA:MISSION\]", RegexOptions.Compiled);

        #endregion

        #region Public-Methods

        /// <summary>
        /// Whether the text carries any mission marker, opening or closing, anywhere. Output that names a
        /// block is a structured plan even when its opening line was lost, so it is never re-read as a
        /// numbered list.
        /// </summary>
        /// <param name="text">Architect output; null is treated as empty.</param>
        /// <returns>True when any marker is present.</returns>
        public static bool ContainsAnyMarker(string? text)
        {
            if (String.IsNullOrEmpty(text)) return false;
            return text.Contains(OpenMarker, StringComparison.Ordinal)
                || text.Contains(EndMarker, StringComparison.Ordinal)
                || text.Contains(LegacyEndMarker, StringComparison.Ordinal);
        }

        /// <summary>Split the text into mission block bodies.</summary>
        /// <param name="text">Architect output; null is treated as empty.</param>
        /// <returns>The block bodies, the text before the first block, and whether a block was left open.</returns>
        public static ArchitectMissionBlockSplit Split(string? text)
        {
            ArchitectMissionBlockSplit split = new ArchitectMissionBlockSplit();
            string source = (text ?? String.Empty).Replace("\r\n", "\n");
            MatchCollection opens = _OpenAtLineStart.Matches(source);
            if (opens.Count == 0)
            {
                split.Preamble = source;
                return split;
            }

            split.Preamble = source.Substring(0, opens[0].Index);
            for (int i = 0; i < opens.Count; i++)
            {
                int bodyStart = opens[i].Index + opens[i].Length;
                int limit = i + 1 < opens.Count ? opens[i + 1].Index : source.Length;
                string region = source.Substring(bodyStart, limit - bodyStart);
                int end = IndexOfEnd(region);
                if (end < 0 && split.FirstUnterminatedIndex < 0) split.FirstUnterminatedIndex = split.Blocks.Count;
                split.Blocks.Add(end < 0 ? region : region.Substring(0, end));
            }
            return split;
        }

        #endregion

        #region Private-Methods

        private static int IndexOfEnd(string region)
        {
            int end = region.IndexOf(EndMarker, StringComparison.Ordinal);
            int legacy = region.IndexOf(LegacyEndMarker, StringComparison.Ordinal);
            if (end < 0) return legacy;
            if (legacy < 0) return end;
            return Math.Min(end, legacy);
        }

        #endregion
    }
}
