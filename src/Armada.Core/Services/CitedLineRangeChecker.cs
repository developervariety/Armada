namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Text.RegularExpressions;
    using Armada.Core.Models;

    /// <summary>
    /// Finds the file line ranges that mission or objective text cites and checks each against the file
    /// as a revision holds it. Text ages: a range drifts past the end of a file that shrank, or away from
    /// the symbol it named once code moved, and a captain then re-measures every citation. A citation is
    /// paired with a symbol when the text writes <c>`Symbol` (`path:line`)</c>, <c>`Symbol` (path:line)</c>
    /// or <c>`Symbol` at `path:line`</c>; the symbol must then appear inside the cited lines.
    /// </summary>
    public static class CitedLineRangeChecker
    {
        #region Public-Members

        /// <summary>
        /// The most citations read from one text.
        /// </summary>
        public const int MaxCitations = 12;

        #endregion

        #region Private-Members

        private static readonly Regex _Citation = new Regex(
            @"(?:`(?<symbol>[A-Za-z_][A-Za-z0-9_.]*)`\s*(?:\(\s*|at\s+))?`?(?<path>[A-Za-z0-9_./-]+\.[A-Za-z0-9]+):(?<start>\d{1,7})(?:-(?<end>\d{1,7}))?",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        #endregion

        #region Public-Methods

        /// <summary>
        /// Read the cited line ranges from text, in order, without duplicates.
        /// </summary>
        /// <param name="text">Mission or objective text.</param>
        /// <returns>The citations; empty when the text cites none.</returns>
        public static List<CitedLineRange> Parse(string? text)
        {
            List<CitedLineRange> results = new List<CitedLineRange>();
            if (String.IsNullOrWhiteSpace(text)) return results;
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match match in _Citation.Matches(text))
            {
                int start = Int32.Parse(match.Groups["start"].Value, CultureInfo.InvariantCulture);
                int end = match.Groups["end"].Success ? Int32.Parse(match.Groups["end"].Value, CultureInfo.InvariantCulture) : start;
                if (start < 1 || end < start) continue;
                CitedLineRange range = new CitedLineRange
                {
                    Path = match.Groups["path"].Value,
                    StartLine = start,
                    EndLine = end,
                    Symbol = match.Groups["symbol"].Success ? match.Groups["symbol"].Value : null
                };
                if (!seen.Add(range.Text + "|" + range.Symbol)) continue;
                results.Add(range);
                if (results.Count >= MaxCitations) break;
            }
            return results;
        }

        /// <summary>
        /// Check one citation against the lines of its file.
        /// </summary>
        /// <param name="range">The citation.</param>
        /// <param name="lines">The file's lines on the revision.</param>
        /// <returns>A one-sentence issue naming the citation, or null when the citation holds.</returns>
        public static string? Evaluate(CitedLineRange range, IReadOnlyList<string> lines)
        {
            if (range == null) throw new ArgumentNullException(nameof(range));
            if (lines == null) throw new ArgumentNullException(nameof(lines));

            if (range.StartLine > lines.Count)
                return "`" + range.Text + "` is past the end of the file (" + lines.Count + " lines).";
            if (String.IsNullOrEmpty(range.Symbol)) return null;

            // A qualified name is cited by its member; the declaration line holds the last segment.
            string symbol = range.Symbol!;
            int dot = symbol.LastIndexOf('.');
            string needle = dot >= 0 && dot < symbol.Length - 1 ? symbol.Substring(dot + 1) : symbol;

            int last = Math.Min(range.EndLine, lines.Count);
            for (int line = range.StartLine; line <= last; line++)
            {
                if (lines[line - 1].Contains(needle, StringComparison.Ordinal)) return null;
            }

            for (int line = 1; line <= lines.Count; line++)
            {
                if (lines[line - 1].Contains(needle, StringComparison.Ordinal))
                    return "`" + symbol + "` is not within `" + range.Text + "`; it is at line " + line + ".";
            }
            return "`" + symbol + "` is not within `" + range.Text + "` and does not appear in the file.";
        }

        #endregion
    }
}
