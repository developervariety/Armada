namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Text;
    using System.Text.RegularExpressions;

    /// <summary>
    /// Renders the failing and skipped tests of a test run from its whole combined output, standard
    /// output and standard error together. A bounded output tail keeps only the last lines, so it can
    /// drop a failing test whose name or exception printed early or on standard error, and it names
    /// only the skips that happen to sit near the end. The digest is read before any bounding: every
    /// failing test is listed with the first exception line printed after it, and the skip list is
    /// either complete or a count with a pointer to the full log.
    /// </summary>
    public static class TestOutcomeDigest
    {
        #region Public-Members

        /// <summary>Largest number of skipped test names the digest lists before it gives a count instead.</summary>
        public const int MaxListedSkips = 100;

        /// <summary>Largest number of characters the rendered digest uses.</summary>
        public const int MaxDigestChars = 7000;

        #endregion

        #region Private-Members

        private const int _ExceptionWindowLines = 12;
        private const int _MaxMessageChars = 300;

        // dotnet VSTest per-test skip line: "Skipped <name> [<duration>]". "Skipped" must be followed
        // by whitespace and the duration marker is required, so neither the "Skipped:" field of the
        // run summary nor a build log sentence that starts with the word is read as a test name.
        private static readonly Regex _DotnetSkippedLinePattern = new Regex(
            @"^[ \t]*Skipped[ \t]+(?<name>\S.*?)[ \t]+\[[^\]]*\][ \t]*$",
            RegexOptions.Compiled);

        // xUnit console line for a skipped test: "[xUnit.net 00:00:01.23]     Name [SKIP]".
        private static readonly Regex _XunitSkippedLinePattern = new Regex(
            @"^[ \t]*\[xUnit\.net[^\]]*\][ \t]+(?<name>\S.*?)[ \t]+\[SKIP\][ \t]*$",
            RegexOptions.Compiled);

        // pytest short-summary skip line: "SKIPPED [1] path:line: reason".
        private static readonly Regex _PytestSkippedLinePattern = new Regex(
            @"^SKIPPED[ \t]+\[\d+\][ \t]+(?<name>\S.*?)[ \t]*$",
            RegexOptions.Compiled);

        // Console runner skip line: "  SKIP  name: reason".
        private static readonly Regex _ConsoleSkippedLinePattern = new Regex(
            @"^[ \t]*SKIP[ \t]+(?<name>\S.*?)[ \t]*$",
            RegexOptions.Compiled);

        // Run summary skip counts: dotnet "Skipped:    17", pytest "3 skipped".
        private static readonly Regex _SummarySkippedCountPattern = new Regex(
            @"(?:\bSkipped:[ \t]*(?<count>\d+))|(?:\b(?<count>\d+)[ \t]+skipped\b)",
            RegexOptions.Compiled);

        // A line that carries an exception or assertion message.
        private static readonly Regex _ExceptionLinePattern = new Regex(
            @"(Exception|Error|Assert|Failure|failed|expected)",
            RegexOptions.Compiled);

        #endregion

        #region Public-Methods

        /// <summary>
        /// Render the failing and skipped tests a run printed.
        /// </summary>
        /// <param name="output">Whole combined standard output and standard error of the run.</param>
        /// <param name="profilePattern">The workflow profile's extra failing-test matcher, or null.</param>
        /// <param name="fullLogPointer">How a reader fetches the complete log, named when a list is cut short.</param>
        /// <returns>The digest text, or null when the output names no failing or skipped test.</returns>
        public static string? Render(string? output, Regex? profilePattern, string fullLogPointer)
        {
            if (String.IsNullOrEmpty(output)) return null;

            string[] lines = output.Replace("\r\n", "\n").Split('\n');
            List<string> failing = new List<string>();
            HashSet<string> failingSeen = new HashSet<string>(StringComparer.Ordinal);
            List<string> skipped = new List<string>();
            HashSet<string> skippedSeen = new HashSet<string>(StringComparer.Ordinal);
            int summarySkipped = 0;
            bool patternTimedOut = false;

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].TrimEnd('\r');
                string? failed = MatchFailure(line, patternTimedOut ? null : profilePattern, out bool timedOut);
                if (timedOut) patternTimedOut = true;
                if (failed != null)
                {
                    if (failingSeen.Add(failed))
                        failing.Add(failed + ": " + FirstExceptionLine(lines, i, failed, profilePattern));
                    continue;
                }

                string? skip = MatchSkip(line);
                if (skip != null)
                {
                    if (skippedSeen.Add(skip)) skipped.Add(skip);
                    continue;
                }

                foreach (Match match in _SummarySkippedCountPattern.Matches(line))
                {
                    if (Int32.TryParse(match.Groups["count"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int count))
                        summarySkipped += count;
                }
            }

            if (failing.Count == 0 && skipped.Count == 0 && summarySkipped == 0) return null;

            StringBuilder sb = new StringBuilder();
            if (failing.Count > 0)
            {
                sb.Append("--- FAILING TESTS (").Append(failing.Count).Append(") ---\n");
                AppendBounded(sb, failing, MaxDigestChars / 2,
                    "failing tests not listed here; full log: " + fullLogPointer);
                if (patternTimedOut)
                    sb.Append("The profile's failing-test pattern timed out, so this list may be incomplete; full log: ")
                        .Append(fullLogPointer).Append('\n');
            }

            int skipTotal = Math.Max(summarySkipped, skipped.Count);
            if (skipTotal > 0)
            {
                sb.Append("--- SKIPPED TESTS (").Append(skipTotal).Append(") ---\n");
                List<string> listed = skipped.Count > MaxListedSkips ? skipped.GetRange(0, MaxListedSkips) : skipped;
                int written = AppendBounded(sb, listed, MaxDigestChars - sb.Length, null);
                if (written < skipTotal)
                {
                    sb.Append(skipTotal).Append(" skipped, ").Append(written).Append(" listed here. Full skip list: ")
                        .Append(fullLogPointer).Append('\n');
                }
            }

            string text = sb.ToString().TrimEnd('\n');
            return text.Length <= MaxDigestChars ? text : text.Substring(0, MaxDigestChars);
        }

        #endregion

        #region Private-Methods

        private static string? MatchFailure(string line, Regex? profilePattern, out bool timedOut)
        {
            timedOut = false;
            try
            {
                return FailedTestNameExtractor.MatchFailureLine(line, profilePattern);
            }
            catch (RegexMatchTimeoutException)
            {
                timedOut = true;
                return FailedTestNameExtractor.MatchFailureLine(line, null);
            }
        }

        private static string? MatchSkip(string line)
        {
            if (String.IsNullOrWhiteSpace(line)) return null;
            foreach (Regex pattern in new[] { _DotnetSkippedLinePattern, _XunitSkippedLinePattern, _PytestSkippedLinePattern, _ConsoleSkippedLinePattern })
            {
                Match match = pattern.Match(line);
                if (!match.Success) continue;
                string name = match.Groups["name"].Value.Trim();
                if (name.Length > 0) return name;
            }
            return null;
        }

        // The message is on the failure line itself when the runner prints "FAIL name: message";
        // otherwise it is the first exception-like line printed after the failure line, before the
        // next test's line.
        private static string FirstExceptionLine(string[] lines, int index, string name, Regex? profilePattern)
        {
            string own = lines[index].TrimEnd('\r');
            int nameAt = own.IndexOf(name, StringComparison.Ordinal);
            if (nameAt >= 0)
            {
                string rest = own.Substring(nameAt + name.Length).Trim();
                if (rest.StartsWith(":", StringComparison.Ordinal) && rest.Length > 1)
                    return Bound(rest.Substring(1).Trim());
            }

            string? fallback = null;
            int end = Math.Min(lines.Length, index + 1 + _ExceptionWindowLines);
            for (int i = index + 1; i < end; i++)
            {
                string candidate = lines[i].TrimEnd('\r').Trim();
                if (candidate.Length == 0 || IsHeader(candidate)) continue;
                if (MatchFailure(lines[i].TrimEnd('\r'), profilePattern, out bool _) != null || MatchSkip(candidate) != null) break;
                if (_ExceptionLinePattern.IsMatch(candidate)) return Bound(candidate);
                if (fallback == null) fallback = candidate;
            }
            return fallback == null ? "(no exception line printed)" : Bound(fallback);
        }

        private static bool IsHeader(string trimmed)
        {
            return trimmed == "Error Message:"
                || trimmed == "Stack Trace:"
                || trimmed == "Standard Output Messages:"
                || trimmed.StartsWith("Traceback (most recent call last)", StringComparison.Ordinal)
                || trimmed.Trim('-', '=').Length == 0;
        }

        private static string Bound(string text)
        {
            return text.Length <= _MaxMessageChars ? text : text.Substring(0, _MaxMessageChars) + "...";
        }

        private static int AppendBounded(StringBuilder sb, List<string> items, int budget, string? overflowNote)
        {
            int used = 0;
            int written = 0;
            foreach (string item in items)
            {
                if (used + item.Length + 1 > budget) break;
                sb.Append(item).Append('\n');
                used += item.Length + 1;
                written++;
            }
            if (written < items.Count && overflowNote != null)
                sb.Append(items.Count - written).Append(' ').Append(overflowNote).Append('\n');
            return written;
        }

        #endregion
    }
}
