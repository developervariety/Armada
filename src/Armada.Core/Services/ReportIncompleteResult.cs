namespace Armada.Core.Services
{
    using System;
    using System.Text.RegularExpressions;

    /// <summary>
    /// The one rule for a report-only Worker that ends <c>[ARMADA:RESULT] COMPLETE</c> while its own report
    /// states that its current-stage acceptance criteria are not met. A criterion that explicitly awaits an
    /// actual pending downstream Judge retains that review obligation without cancelling the Judge.
    /// </summary>
    /// <remarks>
    /// The completion path fails such a stage with a <see cref="FailureReasonPrefix"/> reason that quotes the
    /// captain's statement, so the reviewer stages are cancelled instead of run. Only the final outcome
    /// <c>[ARMADA:RESULT] COMPLETE</c> counts: a stage that ends BLOCKED, refused, or with no result marker is
    /// read by its own rule. A statement that some other subject does not meet a criterion is not matched; the
    /// sentence must say that the acceptance criteria themselves are not met.
    /// </remarks>
    public static class ReportIncompleteResult
    {
        #region Public-Members

        /// <summary>The failure reason prefix of a report-only Worker whose report states its criteria are not met.</summary>
        public const string FailureReasonPrefix = "report_incomplete:";

        /// <summary>The line that introduces the captain's statement inside the failure reason.</summary>
        public const string StatementHeading = "Statement:";

        /// <summary>The largest statement text kept, in characters.</summary>
        public const int MaxStatementLength = 600;

        #endregion

        #region Private-Members

        private const string _CompleteWord = "COMPLETE";

        private static readonly Regex _CriteriaNotMet = new Regex(
            @"\b(?:acceptance\s+criteri(?:a|on)|criterion\s+\d+)\b[^.;\n]{0,60}?\b(?:are|is|were|was|remain|remains)\b[^.;\n]{0,40}?\bnot\s+(?:met|satisfied)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));

        private static readonly Regex _PendingJudgeOnly = new Regex(
            @"\A\s*(?:only\s+)?(?:because|until)\s+(?:(?:the|final|independent)\s+)*Judge(?:\s+(?:review|acceptance|verdict))?\s+(?:(?:is|remains)\s+)?(?:still\s+)?(?:pending|not\s+(?:yet\s+)?(?:complete|finished|done))\s*[.!]?\s*\z",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));

        private static readonly Regex _PendingRoleReviewsOnly = new Regex(
            @"\A\s*(?:at\s+this\s+[A-Za-z]+\s+precursor\s+stage\s+)?(?:only\s+)?because\s+(?:the\s+)?(?:(?<count>[1-9][0-9]?|one|two|three|four|five|six|seven|eight|nine|ten)\s+)?(?:later|downstream|future)\s+(?:role\s+)?reviews\s+(?:do\s+not\s+(?:yet\s+)?exist|have\s+not\s+(?:yet\s+)?run|(?:are|remain)\s+(?:still\s+)?pending)\s*[.!]?\s*\z",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));

        #endregion

        #region Public-Methods

        /// <summary>
        /// Read the captain's statement when the output's final outcome is <c>[ARMADA:RESULT] COMPLETE</c> and the
        /// report states that its acceptance criteria are not met.
        /// </summary>
        /// <param name="agentOutput">Captain output.</param>
        /// <param name="statement">The sentence that states the criteria are not met, bounded to <see cref="MaxStatementLength"/>.</param>
        /// <returns>True when the stage claims COMPLETE for a report it states is unfinished.</returns>
        public static bool TryRead(string? agentOutput, out string statement)
        {
            return TryRead(agentOutput, false, out statement);
        }

        /// <summary>Read unmet current work while retaining the obligation of an actual pending downstream Judge.</summary>
        /// <param name="agentOutput">Captain output.</param>
        /// <param name="hasPendingDownstreamJudge">Whether the current mission has a pending Judge in its dependency chain.</param>
        /// <param name="statement">The bounded statement of unfinished current work.</param>
        /// <returns>True when an unmet criterion is not solely awaiting that configured Judge.</returns>
        public static bool TryRead(string? agentOutput, bool hasPendingDownstreamJudge, out string statement)
        {
            return TryRead(agentOutput, hasPendingDownstreamJudge, 0, out statement);
        }

        /// <summary>Read unmet work while retaining only verified future review obligations.</summary>
        /// <param name="agentOutput">Captain output.</param>
        /// <param name="hasPendingDownstreamJudge">Whether an owned pending Judge depends on this mission through pending stages.</param>
        /// <param name="pendingDownstreamReviewCount">Distinct pending review roles on that dependency path, excluding Workers.</param>
        /// <param name="statement">The bounded statement of unfinished work.</param>
        /// <returns>True when an unmet criterion is not solely awaiting the verified future reviews.</returns>
        public static bool TryRead(string? agentOutput, bool hasPendingDownstreamJudge, int pendingDownstreamReviewCount, out string statement)
        {
            statement = String.Empty;
            if (String.IsNullOrEmpty(agentOutput)) return false;
            if (!ProgressParser.TryFindFinalOutcome(agentOutput, out ProgressParser.ProgressSignal signal, out ProgressParser.MarkerLine _))
                return false;
            if (!String.Equals(signal.Type, "result", StringComparison.Ordinal)) return false;
            if (!ProgressParser.ValueStartsWithWord(signal.Value, _CompleteWord)) return false;

            string text = agentOutput.Replace("**", String.Empty).Replace("__", String.Empty);
            MatchCollection matches;
            try
            {
                matches = _CriteriaNotMet.Matches(text);
                _ = matches.Count;
            }
            catch (RegexMatchTimeoutException)
            {
                return false;
            }
            foreach (Match match in matches)
            {
                int start = SentenceStart(text, match.Index);
                int end = SentenceEnd(text, match.Index + match.Length);
                if (hasPendingDownstreamJudge)
                {
                    string reason = text.Substring(match.Index + match.Length, end - match.Index - match.Length);
                    try
                    {
                        if (_PendingJudgeOnly.IsMatch(reason)) continue;
                        Match futureReviews = _PendingRoleReviewsOnly.Match(reason);
                        if (futureReviews.Success && pendingDownstreamReviewCount > 0
                            && (!futureReviews.Groups["count"].Success
                                || ReadReviewCount(futureReviews.Groups["count"].Value) == pendingDownstreamReviewCount)) continue;
                    }
                    catch (RegexMatchTimeoutException)
                    {
                        statement = "Downstream review classification timed out: "
                            + text.Substring(start, Math.Min(end - start, MaxStatementLength)).Trim();
                        return true;
                    }
                }
                string sentence = text.Substring(start, end - start).Trim();
                statement = sentence.Length <= MaxStatementLength ? sentence : sentence.Substring(0, MaxStatementLength) + "...";
                return true;
            }
            return false;
        }

        /// <summary>
        /// Build the failure reason for a report-only Worker whose report states its criteria are not met.
        /// </summary>
        /// <param name="persona">Stage persona.</param>
        /// <param name="statement">The captain's statement.</param>
        /// <returns>The failure reason.</returns>
        public static string BuildFailureReason(string? persona, string statement)
        {
            string stage = String.IsNullOrWhiteSpace(persona) ? "Worker" : persona!.Trim();
            return FailureReasonPrefix + " the " + stage + " stage ended with [ARMADA:RESULT] COMPLETE, but its report states that its acceptance criteria are not met."
                + " The reviewer stages were not run, because they could only confirm an unfinished report.\n"
                + StatementHeading + "\n" + (statement ?? String.Empty);
        }

        /// <summary>
        /// True when a mission failure reason records a report the captain stated is unfinished.
        /// </summary>
        /// <param name="failureReason">Mission failure reason.</param>
        /// <returns>True for an unfinished report.</returns>
        public static bool IsIncompleteReportFailure(string? failureReason)
        {
            return !String.IsNullOrEmpty(failureReason)
                && failureReason!.StartsWith(FailureReasonPrefix, StringComparison.Ordinal);
        }

        #endregion

        #region Private-Methods

        private static int ReadReviewCount(string value)
        {
            if (Int32.TryParse(value, out int count)) return count;
            return value.ToLowerInvariant() switch
            {
                "one" => 1, "two" => 2, "three" => 3, "four" => 4, "five" => 5,
                "six" => 6, "seven" => 7, "eight" => 8, "nine" => 9, "ten" => 10,
                _ => 0
            };
        }

        private static int SentenceStart(string text, int index)
        {
            for (int i = index - 1; i >= 0; i--)
            {
                char c = text[i];
                if (c == '\n' || c == ';') return i + 1;
                if ((c == '.' || c == '!' || c == '?') && i + 1 < text.Length && Char.IsWhiteSpace(text[i + 1])) return i + 1;
            }
            return 0;
        }

        private static int SentenceEnd(string text, int index)
        {
            for (int i = index; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '\n')
                {
                    int next = i + 1;
                    while (next < text.Length && Char.IsWhiteSpace(text[next])) next++;
                    if (StartsNextStatement(text, next)) return i;
                    i = next - 1;
                    continue;
                }
                if (c == '.' || c == '!' || c == '?') return i + 1;
            }
            return text.Length;
        }

        private static bool StartsNextStatement(string text, int index)
        {
            if (index == text.Length) return true;
            if ((text[index] == '-' || text[index] == '*' || text[index] == '+')
                && index + 1 < text.Length && Char.IsWhiteSpace(text[index + 1]))
            {
                index++;
                while (index < text.Length && Char.IsWhiteSpace(text[index])) index++;
            }
            return text.AsSpan(index).StartsWith("[ARMADA:", StringComparison.OrdinalIgnoreCase)
                || text.AsSpan(index).StartsWith("criterion ", StringComparison.OrdinalIgnoreCase)
                || text.AsSpan(index).StartsWith("acceptance criteri", StringComparison.OrdinalIgnoreCase);
        }

        #endregion
    }
}
