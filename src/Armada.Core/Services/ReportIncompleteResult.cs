namespace Armada.Core.Services
{
    using System;
    using System.Text.RegularExpressions;

    /// <summary>
    /// The one rule for a report-only Worker that ends <c>[ARMADA:RESULT] COMPLETE</c> while its own report
    /// states that its acceptance criteria are not met. The report is unfinished by the captain's own account,
    /// so every reviewer stage after it can only confirm that, and the voyage cannot pass.
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
            @"\bacceptance\s+criteri(?:a|on)\b[^.\n]{0,60}?\b(?:are|is|were|was|remain|remains)\b[^.\n]{0,40}?\bnot\s+(?:met|satisfied)\b",
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
            statement = String.Empty;
            if (String.IsNullOrEmpty(agentOutput)) return false;
            if (!ProgressParser.TryFindFinalOutcome(agentOutput, out ProgressParser.ProgressSignal signal, out ProgressParser.MarkerLine _))
                return false;
            if (!String.Equals(signal.Type, "result", StringComparison.Ordinal)) return false;
            if (!ProgressParser.ValueStartsWithWord(signal.Value, _CompleteWord)) return false;

            string text = agentOutput.Replace("**", String.Empty).Replace("__", String.Empty);
            Match match;
            try
            {
                match = _CriteriaNotMet.Match(text);
            }
            catch (RegexMatchTimeoutException)
            {
                return false;
            }
            if (!match.Success) return false;

            int start = SentenceStart(text, match.Index);
            int end = SentenceEnd(text, match.Index + match.Length);
            string sentence = text.Substring(start, end - start).Trim();
            statement = sentence.Length <= MaxStatementLength ? sentence : sentence.Substring(0, MaxStatementLength) + "...";
            return true;
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

        private static int SentenceStart(string text, int index)
        {
            for (int i = index - 1; i >= 0; i--)
            {
                char c = text[i];
                if (c == '\n') return i + 1;
                if ((c == '.' || c == '!' || c == '?') && i + 1 < text.Length && Char.IsWhiteSpace(text[i + 1])) return i + 1;
            }
            return 0;
        }

        private static int SentenceEnd(string text, int index)
        {
            for (int i = index; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '\n') return i;
                if (c == '.' || c == '!' || c == '?') return i + 1;
            }
            return text.Length;
        }

        #endregion
    }
}
