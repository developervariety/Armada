namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.Json.Serialization;
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

        /// <summary>The truncation reason of an output whose captured head was dropped at the stream capture limit.</summary>
        public const string StreamCaptureLimitReason = "stream_capture_limit";

        /// <summary>The truncation reason of an output that holds no closing result marker.</summary>
        public const string ClosingMarkerMissingReason = "closing_marker_missing";

        /// <summary>The largest output tail quoted in a truncation failure reason, in characters.</summary>
        public const int MaxTruncationTailLength = 200;

        #endregion

        #region Private-Members

        private const string _CompleteWord = "COMPLETE";

        private static readonly Regex _CriteriaNotMet = new Regex(
            @"\b(?:(?:acceptance\s+)?criterion\s+(?<number>\d+)|acceptance\s+criteri(?:a|on))\b[^.;\n]{0,60}?\b(?:are|is|were|was|remain|remains)\b[^.;\n]{0,40}?\bnot\s+(?:met|satisfied)\b",
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

        private static readonly Regex _ProcessAssertionSeparator = new Regex(
            @"\s*,?\s*\b(?:although|while|and)\b\s+",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));

        private static readonly Regex _ProcessAssertion = new Regex(
            @"\A(?:only\s+this\s+[A-Za-z]+\s+(?:artifact|report)\s+exists"
            + @"|(?:the\s+)?(?:[A-Za-z]+\s+){0,2}(?:checkouts?|worktrees?)\s+shows?\s+no\s+(?:tracked\s+)?changes?"
            + @"|this\s+report\s+contains\s+no\s+(?:(?:copied|vendor|restricted)\s+)?(?:bod(?:y|ies)|values?|cop(?:y|ies)|text)(?:\s+or\s+(?:(?:copied|vendor|restricted)\s+)?(?:bod(?:y|ies)|values?|cop(?:y|ies)|text))*)\z",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));

        private static readonly Regex _PendingRevisingStage = new Regex(
            @"\A(?<first>.+?\s+have\s+not\s+(?:yet\s+)?run),\s+and\s+(?:the\s+)?(?:additional\s+)?Revising\s*Worker\s+stage\s+(?:has|have)\s+not\s+(?:yet\s+)?run[.!]?\s*\z",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));

        #endregion

        internal sealed class PendingReviewObligations
        {
            internal string CurrentPersona { get; }
            internal IReadOnlyDictionary<string, string> Duties { get; }
            internal int ReviewCount => Duties.Values.Where(IsReviewRole).Distinct(StringComparer.OrdinalIgnoreCase).Count();

            internal PendingReviewObligations(string? currentPersona, IDictionary<string, string> duties)
            {
                CurrentPersona = PersonaCatalog.NormalizeName(currentPersona);
                Duties = new Dictionary<string, string>(duties, StringComparer.Ordinal);
            }

            internal bool Contains(string persona) => Duties.Values.Any(value => PersonaCatalog.Matches(value, persona));

            private static bool IsReviewRole(string persona) => PersonaCatalog.Matches(persona, "PortingReferenceAnalyst")
                || PersonaCatalog.Matches(persona, PersonaCatalog.TestEngineer)
                || PersonaCatalog.Matches(persona, PersonaCatalog.Linter)
                || PersonaCatalog.Matches(persona, PersonaCatalog.Judge);
        }

        [JsonConverter(typeof(JsonStringEnumConverter))]
        private enum ObligationKindEnum { Blocking, FutureOnly, BareStage }

        private sealed class CriterionAssertion
        {
            internal string Number { get; }
            internal string Text { get; }
            internal ObligationKindEnum Kind { get; }

            internal CriterionAssertion(string number, string text, ObligationKindEnum kind)
            {
                Number = number;
                Text = text;
                Kind = kind;
            }
        }

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
            return TryRead(agentOutput, hasPendingDownstreamJudge, pendingDownstreamReviewCount, null, out statement);
        }

        internal static bool TryRead(string? agentOutput, PendingReviewObligations? obligations, out string statement)
        {
            return TryRead(agentOutput, obligations?.Contains(PersonaCatalog.Judge) == true,
                obligations?.ReviewCount ?? 0, obligations, out statement);
        }

        private static bool TryRead(string? agentOutput, bool hasPendingDownstreamJudge, int pendingDownstreamReviewCount,
            PendingReviewObligations? obligations, out string statement)
        {
            statement = String.Empty;
            if (String.IsNullOrEmpty(agentOutput)) return false;
            if (!ProgressParser.TryFindFinalOutcome(agentOutput, out ProgressParser.ProgressSignal signal, out ProgressParser.MarkerLine finalOutcome))
                return false;
            if (!String.Equals(signal.Type, "result", StringComparison.Ordinal)) return false;
            if (!ProgressParser.ValueStartsWithWord(signal.Value, _CompleteWord)) return false;

            string text = agentOutput.Replace("**", String.Empty).Replace("__", String.Empty);
            int markerStart = finalOutcome.LineStart;
            while (markerStart < agentOutput.Length && Char.IsWhiteSpace(agentOutput[markerStart])) markerStart++;
            int resultIndex = agentOutput.Substring(0, markerStart).Replace("**", String.Empty).Replace("__", String.Empty).Length;
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
            List<CriterionAssertion> assertions = new List<CriterionAssertion>();
            for (int matchIndex = 0; matchIndex < matches.Count; matchIndex++)
            {
                Match match = matches[matchIndex];
                int start = SentenceStart(text, match.Index);
                int nextCriterion = matchIndex + 1 < matches.Count ? matches[matchIndex + 1].Index : text.Length;
                int end = SentenceEnd(text, match.Index + match.Length, nextCriterion, resultIndex);
                ObligationKindEnum kind = ObligationKindEnum.Blocking;
                if (hasPendingDownstreamJudge)
                {
                    string reason = text.Substring(match.Index + match.Length, end - match.Index - match.Length);
                    try
                    {
                        if (obligations == null)
                        {
                            if (_PendingJudgeOnly.IsMatch(reason)) kind = ObligationKindEnum.FutureOnly;
                            Match futureReviews = _PendingRoleReviewsOnly.Match(reason);
                            if (futureReviews.Success && pendingDownstreamReviewCount > 0
                                && (!futureReviews.Groups["count"].Success
                                    || ReadReviewCount(futureReviews.Groups["count"].Value) == pendingDownstreamReviewCount)) kind = ObligationKindEnum.FutureOnly;
                        }
                        else
                        {
                            kind = ClassifyObligation(reason, obligations);
                            if (kind == ObligationKindEnum.BareStage)
                            {
                                int tailEnd = AssociatedParagraphEnd(text, end, nextCriterion, resultIndex);
                                string tail = text.Substring(end, tailEnd - end).Trim();
                                if (tail.Length > 0)
                                {
                                    bool future = false;
                                    foreach (string clause in Regex.Split(tail, @"(?<=[.!?])\s+", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
                                    {
                                        if (IsFutureClause(clause, obligations)) future = true;
                                        else if (!Regex.IsMatch(clause, @"\A(?:The\s+)?parent\s+objective\s+(?:is|remains)\s+open[.!]?\s*\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
                                        {
                                            kind = ObligationKindEnum.Blocking;
                                            break;
                                        }
                                    }
                                    if (kind != ObligationKindEnum.Blocking && future) kind = ObligationKindEnum.FutureOnly;
                                    end = tailEnd;
                                }
                            }
                        }
                    }
                    catch (RegexMatchTimeoutException)
                    {
                        statement = "Downstream review classification timed out: "
                            + text.Substring(start, Math.Min(end - start, MaxStatementLength)).Trim();
                        return true;
                    }
                }
                string sentence = text.Substring(start, end - start).Trim();
                assertions.Add(new CriterionAssertion(match.Groups["number"].Value, sentence, kind));
            }
            HashSet<string> explained = assertions.Where(item => item.Kind == ObligationKindEnum.FutureOnly && item.Number.Length > 0)
                .Select(item => item.Number).ToHashSet(StringComparer.Ordinal);
            foreach (CriterionAssertion assertion in assertions)
            {
                if (assertion.Kind == ObligationKindEnum.FutureOnly) continue;
                if (assertion.Kind == ObligationKindEnum.BareStage && assertion.Number.Length > 0 && explained.Contains(assertion.Number)) continue;
                statement = assertion.Text.Length <= MaxStatementLength ? assertion.Text : assertion.Text.Substring(0, MaxStatementLength) + "...";
                return true;
            }
            return false;
        }

        /// <summary>
        /// Read whether a finished report-only Worker's output was cut off: the stream capture kept only a tail,
        /// or no closing <c>[ARMADA:RESULT]</c> or <c>[ARMADA:VERDICT]</c> marker ends the report.
        /// </summary>
        /// <param name="agentOutput">Captain output. Empty output is read by the no-op completion rule, not here.</param>
        /// <param name="reason">The named truncation reason: <see cref="StreamCaptureLimitReason"/> or <see cref="ClosingMarkerMissingReason"/>.</param>
        /// <returns>True when the output is truncated.</returns>
        public static bool TryReadTruncation(string? agentOutput, out string reason)
        {
            reason = String.Empty;
            if (String.IsNullOrWhiteSpace(agentOutput)) return false;
            if (agentOutput!.StartsWith(MissionOutputArtifact.StreamTruncationMarker, StringComparison.Ordinal))
            {
                reason = StreamCaptureLimitReason;
                return true;
            }
            if (ProgressParser.TryFindFinalOutcome(agentOutput, out _, out _)) return false;
            reason = ClosingMarkerMissingReason;
            return true;
        }

        /// <summary>
        /// Build the failure reason for a report-only Worker whose output is truncated.
        /// </summary>
        /// <param name="persona">Stage persona.</param>
        /// <param name="truncationReason">The named reason from <see cref="TryReadTruncation"/>.</param>
        /// <param name="agentOutput">Captain output; its last <see cref="MaxTruncationTailLength"/> characters are quoted.</param>
        /// <returns>The failure reason.</returns>
        public static string BuildTruncatedFailureReason(string? persona, string truncationReason, string? agentOutput)
        {
            string stage = String.IsNullOrWhiteSpace(persona) ? "Worker" : persona!.Trim();
            string output = (agentOutput ?? String.Empty).TrimEnd();
            string tail = output.Length <= MaxTruncationTailLength ? output : "..." + output.Substring(output.Length - MaxTruncationTailLength);
            return FailureReasonPrefix + " the " + stage + " stage ended, but its report output is truncated (" + truncationReason + ")."
                + " The reviewer stages were not run, because they could only confirm a cut report.\n"
                + StatementHeading + "\n" + tail;
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

        private static ObligationKindEnum ClassifyObligation(string reason, PendingReviewObligations obligations)
        {
            string value = reason.Trim();
            Match stage = Regex.Match(value, @"\Aat\s+(?:this|the)\s+(?<role>(?:[A-Za-z]+\s+){0,3})stage\b\s*",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            if (stage.Success)
            {
                string role = Regex.Replace(stage.Groups["role"].Value, @"\b(?:current|precursor)\b", String.Empty,
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)).Trim();
                if (role.Length > 0 && !PersonaCatalog.Matches(role, obligations.CurrentPersona)) return ObligationKindEnum.Blocking;
                value = value.Substring(stage.Length).Trim();
            }
            if (value.Trim('.', '!', '?', ' ', '\r', '\n').Length == 0) return ObligationKindEnum.BareStage;
            if (_PendingJudgeOnly.IsMatch(value)) return ObligationKindEnum.FutureOnly;
            value = Regex.Replace(value, @"\A(?:only\s+)?because\s+", String.Empty,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            return IsFutureClause(value, obligations) ? ObligationKindEnum.FutureOnly : ObligationKindEnum.Blocking;
        }

        private static bool IsFutureClause(string clause, PendingReviewObligations obligations)
        {
            int semicolon = clause.IndexOf(';');
            if (semicolon >= 0)
            {
                if (!IsProcessStatement(clause.Substring(semicolon + 1))) return false;
                clause = clause.Substring(0, semicolon);
            }
            Match revising = _PendingRevisingStage.Match(clause.Trim());
            if (revising.Success)
                return obligations.Contains(PersonaCatalog.Worker) && IsFutureClause(revising.Groups["first"].Value + ".", obligations);
            Match details = Regex.Match(clause.Trim(), @"\A(?<summary>[^:]+):\s*(?<roles>[A-Za-z ,]+?)\s+have\s+not\s+(?:yet\s+)?reviewed\s+(?:this|the)\s+report,\s+and\s+(?:the\s+)?Revising\s*Worker\s+is\s+a\s+separate\s+later\s+stage\s+and\s+has\s+not\s+(?:yet\s+)?run[.!]?\s*\z",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            if (details.Success)
            {
                string roles = Regex.Replace(details.Groups["roles"].Value, @",\s+and\s+", ", ",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
                return IsFutureClause(details.Groups["summary"].Value, obligations)
                    && IsFutureClause(roles + " and RevisingWorker reviews remain pending.", obligations);
            }
            Match pending = Regex.Match(clause.Trim(), @"\A(?<subject>.+?)\s+(?:do\s+not\s+(?:yet\s+)?exist|have\s+not\s+(?:yet\s+)?run|(?:are|remain)\s+(?:still\s+)?pending)[.!]?\s*\z",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            if (!pending.Success) return false;
            string subject = pending.Groups["subject"].Value;
            Match revision = Regex.Match(subject, @"\s+and\s+(?:the\s+)?(?:additional\s+)?Revising\s*Worker\s+stage\z",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            if (revision.Success)
            {
                if (!obligations.Contains(PersonaCatalog.Worker)) return false;
                subject = subject.Substring(0, revision.Index);
            }
            Match generic = Regex.Match(subject, @"\A(?:the\s+)?(?:(?<count>[1-9][0-9]?|one|two|three|four|five|six|seven|eight|nine|ten)\s+)?(?:later|downstream|future)\s+(?:role\s+)?reviews\z",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            if (generic.Success) return obligations.ReviewCount > 0
                && (!generic.Groups["count"].Success || ReadReviewCount(generic.Groups["count"].Value) == obligations.ReviewCount);
            Match named = Regex.Match(subject, @"\A(?:the\s+)?(?:current\s+)?(?<roles>[A-Za-z ,]+?)\s+reviews\z",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            if (!named.Success) return false;
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string item in Regex.Split(named.Groups["roles"].Value, @"\s*,\s*|\s+and\s+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
            {
                string role = item.Trim();
                if (String.Equals(role.Replace(" ", String.Empty), "RevisingWorker", StringComparison.OrdinalIgnoreCase)) role = PersonaCatalog.Worker;
                else if (!PersonaCatalog.Matches(role, "PortingReferenceAnalyst") && !PersonaCatalog.Matches(role, PersonaCatalog.TestEngineer)
                    && !PersonaCatalog.Matches(role, PersonaCatalog.Linter) && !PersonaCatalog.Matches(role, PersonaCatalog.Judge)) return false;
                role = PersonaCatalog.NormalizeName(role);
                if (!seen.Add(role) || !obligations.Contains(role)) return false;
            }
            return seen.Count > 0;
        }

        private static bool IsProcessStatement(string tail)
        {
            string value = tail.Trim().TrimEnd('.', '!').Trim();
            if (value.Length == 0 || value.Contains(';')) return false;
            foreach (string assertion in _ProcessAssertionSeparator.Split(value))
                if (!_ProcessAssertion.IsMatch(assertion.Trim())) return false;
            return true;
        }

        private static int AssociatedParagraphEnd(string text, int start, int nextCriterion, int resultIndex)
        {
            int end = text.IndexOf("\n\n", start, StringComparison.Ordinal);
            if (end < 0) end = text.Length;
            foreach (int boundary in new[] { nextCriterion, resultIndex })
            {
                if (boundary < start || boundary >= end) continue;
                int lineStart = text.LastIndexOf('\n', boundary);
                end = lineStart >= start && text.Substring(lineStart, boundary - lineStart).Trim(' ', '\t', '\r', '\n', '-', '*', '+').Length == 0 ? lineStart : boundary;
            }
            return end;
        }

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

        private static int SentenceEnd(string text, int index, int nextCriterion, int resultIndex)
        {
            for (int i = index; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '\n')
                {
                    int next = i + 1;
                    while (next < text.Length && Char.IsWhiteSpace(text[next])) next++;
                    if (StartsNextStatement(text, next, nextCriterion, resultIndex)) return i;
                    i = next - 1;
                    continue;
                }
                if (c == '.' || c == '!' || c == '?') return i + 1;
            }
            return text.Length;
        }

        private static bool StartsNextStatement(string text, int index, int nextCriterion, int resultIndex)
        {
            if (index == text.Length) return true;
            if ((text[index] == '-' || text[index] == '*' || text[index] == '+')
                && index + 1 < text.Length && Char.IsWhiteSpace(text[index + 1]))
            {
                index++;
                while (index < text.Length && Char.IsWhiteSpace(text[index])) index++;
            }
            return index == nextCriterion || index == resultIndex;
        }

        #endregion
    }
}
