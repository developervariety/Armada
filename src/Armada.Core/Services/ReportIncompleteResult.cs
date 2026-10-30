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
    /// read by its own rule. A line holding a criterion reference followed by a NOT MET phrase is a statement; only a
    /// prose line that defers the criterion to later review stages and carries nothing else hands off.
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

        private const string _NumberWord = @"(?:\d+|one|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve|thirteen|fourteen|fifteen|sixteen|seventeen|eighteen|nineteen|twenty)";

        private const string _ReferencePattern = @"(?:(?:acceptance\s+)?criteri(?:on|a)\s*#?\s*(?<number>" + _NumberWord + @"(?:\s*(?:-|\u2013|to|,|and|&)\s*#?" + _NumberWord + @")*)\b"
            + @"|\bAC[-\s]?(?<number>\d+(?:\s*(?:-|\u2013|,|and|&)\s*\d+)*)\b"
            + @"|acceptance\s+criteri(?:a|on)\b)";

        private static readonly Regex _CriterionReference = new Regex(
            @"\b" + _ReferencePattern,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));

        private const string _StatusModifiers = @"(?:\(?(?:yet|fully|currently|been)\)?[\s-]+)*";

        private const string _StatusWords = @"(?:met|satisfied|complete|completed|done|achieved|fulfilled)";

        private const string _NotMetPattern = @"(?<ok>(?:(?:has|have)\s+)?[*_]?not[*_]?[\s-]+" + _StatusModifiers + _StatusWords
            + @"|unmet|unsatisfied|unfulfilled|incomplete"
            + @"|(?:isn|aren|wasn|weren|hasn|haven)['\u2019]t\s+" + _StatusModifiers + _StatusWords
            + @"|(?<=:\s{0,2})(?:FAIL(?:ED)?|\u2717|\u2718|\u274C))"
            + @"|(?<bad>(?:only\s+)?(?:partially|partly)\s+met|(?:does|do|did)\s+not\s+(?:yet\s+)?meet|(?:doesn|don|didn)['\u2019]t\s+(?:yet\s+)?meet"
            + @"|cannot\s+be\s+met|can['\u2019]t\s+be\s+met)";

        private static readonly Regex _NotMetPhrase = new Regex(
            @"(?<![A-Za-z0-9])(?:" + _NotMetPattern + @")(?![A-Za-z0-9])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));

        private static readonly Regex _StatusSuffix = new Regex(
            @"(?:\s(?:--|\u2014|\u2013|-)\s+|:\s*)\z",
            RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));

        private static readonly Regex _ListItemMarker = new Regex(
            @"\A\s*(?:(?:[-*+>]|\d+[.)]|\[[ xX]\])\s+)+",
            RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));

        private static readonly Regex _ParentheticalReference = new Regex(
            @"\A\s*(?:see|per|cf|evidence|ref)\b|\b[\w-]+\.[A-Za-z]{1,5}\b|[\\/]|\b\d+(?:\.\d+)+\b|(?:\bsection|\bsec|\u00A7|\btable|\brow|\bstep|\bline)\s*\d",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));

        private static readonly HashSet<string> _ParentheticalRoleWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "final", "later", "independent", "review", "reviews", "reviewer", "reviewers", "stage", "stages", "judge", "testengineer",
            "linter", "worker", "portingreferenceanalyst", "research", "audit", "architect", "current", "precursor", "additional",
            "revising", "downstream", "future", "role", "roles", "and", "the", "of", "this", "a"
        };

        private static readonly Regex _GitStatusClause = new Regex(
            @"`git status --porcelain=v1` returned no output in (?:\.{0,2}/)?[A-Z][\w.-]*(?:/[\w.-]+)*(?:(?:,\s*(?:and\s+)?|\s+and\s+)(?:\.{0,2}/)?[A-Z][\w.-]*(?:/[\w.-]+)*)*",
            RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));

        private const int _MaxReferenceGap = 120;

        private static readonly Regex _DefectWord = new Regex(
            @"\b(?:unfinished|incomplete|partial(?:ly)?|half|missing|not\s+written|unimplemented|todo|broken|fails?|failing|stub|placeholder|wip)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));

        private static readonly Regex _AllowedLabel = new Regex(
            @"\A(?:\[ARMADA:[A-Za-z_]+\]|(?:note|result|status|summary|acceptance\s+criteria|criteria|current\s+stage|at\s+this\s+stage)\s*:)\s*",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));

        private static readonly Regex _MetClause = new Regex(
            @"\A(?:(?:the|all|current-stage|current)\s+)*" + _ReferencePattern + @"\s+(?:is|are)\s+met\b(?:\s*\([^)\n]*\))?",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));

        private static readonly Regex _PrefixConnective = new Regex(
            @"\A(?:and|but|the|this|that|current-stage|current|stage|worker|report|our|honestly|therefore|thus|however|so|also|here|overall|finally)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));

        private static readonly HashSet<string> _StatusConnectiveWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "are", "is", "were", "was", "be", "been", "being", "remain", "remains", "has", "have", "isn't", "aren't", "wasn't", "weren't",
            "hasn't", "haven't", "not", "yet", "fully", "currently", "met", "satisfied", "unmet", "unsatisfied", "honestly", "still",
            "also", "now", "therefore", "thus"
        };

        private static readonly Regex _PendingJudgeOnly = new Regex(
            @"\A\s*(?:only\s+)?(?:because|until)\s+(?:(?:the|final|independent)\s+)*Judge(?:\s+(?:review|acceptance|verdict))?\s+(?:(?:(?:is|remains)\s+)?(?:still\s+)?(?:pending|not\s+(?:yet\s+)?(?:complete|finished|done))|has\s+not\s+(?:yet\s+)?run)\s*[.!]?\s*\z",
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

        private const string _WithheldContent = @"(?:(?:copied|vendor|restricted)[\s-]+)+(?:body[\s-]+)?(?:bod(?:y|ies)|values?|cop(?:y|ies)|text)";

        private static readonly Regex _ProcessAssertion = new Regex(
            @"\A(?:only\s+this\s+(?:Worker|Research|Audit)\s+(?:artifact|report)\s+exists"
            + @"|(?:the\s+)?(?:(?:all|both|two|three|four|five|six|\d+)\s+)?(?:checkouts?|worktrees?)\s+shows?\s+no\s+(?:tracked\s+)?changes?"
            + @"|this\s+report\s+contains\s+no\s+" + _WithheldContent + @"(?:\s+or\s+" + _WithheldContent + @")*)\z",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));

        private static readonly Regex _ClosingMarkerLine = new Regex(
            @"\A[>*_`\-\s]*\[ARMADA:(?:RESULT|VERDICT)\]",
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

        private sealed class CriterionCandidate
        {
            internal int ReferenceIndex { get; }
            internal int ReferenceEnd { get; }
            internal int PhraseIndex { get; }
            internal int PhraseEnd { get; }
            internal string Number { get; }
            internal bool Acceptable { get; }

            internal CriterionCandidate(int referenceIndex, int referenceEnd, int phraseIndex, int phraseEnd, string number, bool acceptable)
            {
                ReferenceIndex = referenceIndex;
                ReferenceEnd = referenceEnd;
                PhraseIndex = phraseIndex;
                PhraseEnd = phraseEnd;
                Number = number;
                Acceptable = acceptable;
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
            string output = agentOutput.Replace("\r\n", "\n").Replace('\r', '\n');
            if (!ProgressParser.TryFindFinalOutcome(output, out ProgressParser.ProgressSignal signal, out ProgressParser.MarkerLine finalOutcome))
                return false;
            if (!String.Equals(signal.Type, "result", StringComparison.Ordinal)) return false;
            if (!ProgressParser.ValueStartsWithWord(signal.Value, _CompleteWord)) return false;

            string text = output.Replace("**", String.Empty).Replace("__", String.Empty);
            int markerStart = finalOutcome.LineStart;
            while (markerStart < output.Length && Char.IsWhiteSpace(output[markerStart])) markerStart++;
            int resultIndex = output.Substring(0, markerStart).Replace("**", String.Empty).Replace("__", String.Empty).Length;
            List<CriterionCandidate> candidates;
            try
            {
                candidates = FindCandidates(text);
            }
            catch (RegexMatchTimeoutException)
            {
                statement = "Criterion detection timed out.";
                return true;
            }
            List<CriterionAssertion> assertions = new List<CriterionAssertion>();
            for (int matchIndex = 0; matchIndex < candidates.Count; matchIndex++)
            {
                CriterionCandidate match = candidates[matchIndex];
                int start = SentenceStart(text, match.ReferenceIndex);
                int nextCriterion = matchIndex + 1 < candidates.Count ? candidates[matchIndex + 1].ReferenceIndex : text.Length;
                int end = SentenceEnd(text, match.PhraseEnd, nextCriterion, resultIndex);
                ObligationKindEnum kind = ObligationKindEnum.Blocking;
                if (hasPendingDownstreamJudge)
                {
                    string reason = text.Substring(match.PhraseEnd, end - match.PhraseEnd);
                    try
                    {
                        if (match.Acceptable && IsSubjectOnlyStatement(text, match))
                        {
                            if (obligations == null)
                            {
                                if (_PendingJudgeOnly.IsMatch(reason)) kind = ObligationKindEnum.FutureOnly;
                                Match futureReviews = _PendingRoleReviewsOnly.Match(reason);
                                if (futureReviews.Success && pendingDownstreamReviewCount > 0
                                    && (!futureReviews.Groups["count"].Success
                                        || ReadReviewCount(futureReviews.Groups["count"].Value) == pendingDownstreamReviewCount)) kind = ObligationKindEnum.FutureOnly;
                                if (kind == ObligationKindEnum.FutureOnly)
                                {
                                    int tailEnd = ItemTailEnd(text, match, end, nextCriterion, resultIndex);
                                    if (NormalizeTail(text.Substring(end, tailEnd - end)).Length > 0) kind = ObligationKindEnum.Blocking;
                                    end = tailEnd;
                                }
                            }
                            else
                            {
                                kind = ClassifyObligation(reason, obligations);
                                if (kind == ObligationKindEnum.BareStage)
                                {
                                    int tailEnd = ItemTailEnd(text, match, end, nextCriterion, resultIndex);
                                    string tail = NormalizeTail(text.Substring(end, tailEnd - end));
                                    if (tail.Length > 0)
                                    {
                                        bool future = false;
                                        foreach (string clause in Regex.Split(tail, @"(?<=[.!?])\s+", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
                                        {
                                            if (IsFutureClause(clause, obligations)) future = true;
                                            else if (!IsParentObjectiveOpen(clause))
                                            {
                                                kind = ObligationKindEnum.Blocking;
                                                break;
                                            }
                                        }
                                        if (kind != ObligationKindEnum.Blocking && future) kind = ObligationKindEnum.FutureOnly;
                                        end = tailEnd;
                                    }
                                }
                                else if (kind == ObligationKindEnum.FutureOnly)
                                {
                                    int tailEnd = ItemTailEnd(text, match, end, nextCriterion, resultIndex);
                                    if (!IsFutureOnlyTail(NormalizeTail(text.Substring(end, tailEnd - end)), obligations)) kind = ObligationKindEnum.Blocking;
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
                assertions.Add(new CriterionAssertion(match.Number, sentence, kind));
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
            if (HasClosingMarker(agentOutput)) return false;
            reason = ClosingMarkerMissingReason;
            return true;
        }

        // A closing marker may carry Markdown decoration; a marker quoted inside a code fence is an example, not the close.
        private static bool HasClosingMarker(string output)
        {
            bool fenced = false;
            foreach (string raw in output.Split('\n'))
            {
                string line = raw.Trim();
                if (line.StartsWith("```", StringComparison.Ordinal) || line.StartsWith("~~~", StringComparison.Ordinal))
                {
                    fenced = !fenced;
                    continue;
                }
                if (!fenced && _ClosingMarkerLine.IsMatch(line)) return true;
            }
            return false;
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
            value = _GitStatusClause.Replace(value, "the checkouts show no tracked change");
            if (value.Length == 0 || value.Contains(';')) return false;
            foreach (string assertion in _ProcessAssertionSeparator.Split(value))
                if (!_ProcessAssertion.IsMatch(assertion.Trim())) return false;
            return true;
        }

        // Every line holding a criterion reference and a NOT MET phrase is a candidate, however the phrase is
        // worded; only a prose line that fully parses as a future-review deferral can hand off.
        private static List<CriterionCandidate> FindCandidates(string text)
        {
            List<CriterionCandidate> candidates = new List<CriterionCandidate>();
            int lineStart = 0;
            while (lineStart <= text.Length)
            {
                int lineEnd = text.IndexOf('\n', lineStart);
                if (lineEnd < 0) lineEnd = text.Length;
                if (lineEnd > lineStart) ScanLine(text, lineStart, lineEnd, candidates);
                if (lineEnd >= text.Length) break;
                lineStart = lineEnd + 1;
            }
            return candidates;
        }

        private static void ScanLine(string text, int lineStart, int lineEnd, List<CriterionCandidate> candidates)
        {
            string line = text.Substring(lineStart, lineEnd - lineStart);
            int firstOnLine = candidates.Count;
            MatchCollection phrases = _NotMetPhrase.Matches(line);
            if (phrases.Count == 0) return;
            MatchCollection references = _CriterionReference.Matches(line);
            Match marker = _ListItemMarker.Match(line);
            bool table = line.TrimStart().StartsWith("|", StringComparison.Ordinal);
            HashSet<int> used = new HashSet<int>();
            foreach (Match phrase in phrases)
            {
                bool ok = phrase.Groups["ok"].Success;
                int phraseEnd = phrase.Index + phrase.Length;
                if (phraseEnd < line.Length && line[phraseEnd] == ':') phraseEnd++;
                Match? subject = null;
                bool inParentheses = ParenthesisDepth(line, phrase.Index) > 0;
                foreach (Match reference in references)
                    if (reference.Index + reference.Length <= phrase.Index && (inParentheses || ParenthesisDepth(line, reference.Index) == 0)) subject = reference;
                if (subject == null)
                    foreach (Match reference in references)
                        if (reference.Index + reference.Length <= phrase.Index) subject = reference;
                if (subject != null)
                {
                    string gap = line.Substring(subject.Index + subject.Length, phrase.Index - subject.Index - subject.Length);
                    if (gap.Length <= _MaxReferenceGap && !HasSentenceBreak(gap))
                    {
                        if (used.Add(subject.Index))
                            candidates.Add(new CriterionCandidate(lineStart + subject.Index, lineStart + subject.Index + subject.Length,
                                lineStart + phrase.Index, lineStart + phraseEnd, subject.Groups["number"].Value, ok && !table));
                        continue;
                    }
                }
                if (ok && marker.Success && !table)
                {
                    Match suffix = _StatusSuffix.Match(line.Substring(0, phrase.Index));
                    if (suffix.Success && suffix.Index > marker.Length)
                    {
                        if (used.Add(marker.Length))
                            candidates.Add(new CriterionCandidate(lineStart + marker.Length, lineStart + suffix.Index,
                                lineStart + phrase.Index, lineStart + phraseEnd, String.Empty, true));
                        continue;
                    }
                }
                if (!ok) continue;
                foreach (Match reference in references)
                {
                    if (reference.Index < phrase.Index + phrase.Length || ParenthesisDepth(line, reference.Index) != 0) continue;
                    string gap = line.Substring(phrase.Index + phrase.Length, reference.Index - phrase.Index - phrase.Length);
                    if (gap.Length <= _MaxReferenceGap && !HasSentenceBreak(gap))
                        candidates.Add(new CriterionCandidate(lineStart + reference.Index, lineStart + reference.Index + reference.Length,
                            lineStart + phrase.Index, lineStart + phraseEnd, reference.Groups["number"].Value, false));
                    break;
                }
            }
            candidates.Sort(firstOnLine, candidates.Count - firstOnLine, Comparer<CriterionCandidate>.Create((left, right) => left.ReferenceIndex.CompareTo(right.ReferenceIndex)));
        }

        private static int ParenthesisDepth(string line, int index)
        {
            int depth = 0;
            for (int i = 0; i < index; i++)
            {
                if (line[i] == '(') depth++;
                else if (line[i] == ')' && depth > 0) depth--;
            }
            return depth;
        }

        // A sentence end outside a parenthetical, or a semicolon, separates a reference from a phrase; a period
        // followed by a non-space character (a file name or version) does not.
        private static bool HasSentenceBreak(string gap)
        {
            int depth = 0;
            for (int i = 0; i < gap.Length; i++)
            {
                char c = gap[i];
                if (c == '(') depth++;
                else if (c == ')' && depth > 0) depth--;
                else if (depth == 0 && c == ';') return true;
                else if (depth == 0 && (c == '.' || c == '!' || c == '?') && (i + 1 == gap.Length || Char.IsWhiteSpace(gap[i + 1]))) return true;
            }
            return false;
        }

        // A deferral passes only when its line holds the criterion subject and status words alone: text before the
        // subject is a label, complete MET clauses, or connective words; text between the subject and the phrase
        // is status words or one parenthetical without a defect word.
        private static bool IsSubjectOnlyStatement(string text, CriterionCandidate candidate)
        {
            int lineStart = candidate.ReferenceIndex == 0 ? 0 : text.LastIndexOf('\n', candidate.ReferenceIndex - 1) + 1;
            string prefix = text.Substring(lineStart, candidate.ReferenceIndex - lineStart);
            if (!IsAllowedPrefix(prefix)) return false;
            string between = text.Substring(candidate.ReferenceEnd, candidate.PhraseIndex - candidate.ReferenceEnd).Replace('’', '\'');
            foreach (Match parenthetical in Regex.Matches(between, @"\([^)\n]*\)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
                if (!IsAllowedParenthetical(parenthetical.Value)) return false;
            between = Regex.Replace(between, @"\([^)\n]*\)", " ", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            foreach (Match word in Regex.Matches(between, @"[A-Za-z0-9]+(?:'[A-Za-z0-9]+)*", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
                if (!_StatusConnectiveWords.Contains(word.Value)) return false;
            return true;
        }

        // Text before the subject that ends a complete sentence is a prior sentence, out of this detector's scope.
        private static int LastSentenceBoundary(string prefix)
        {
            int depth = 0;
            int boundary = 0;
            for (int i = 0; i < prefix.Length; i++)
            {
                char c = prefix[i];
                if (c == '(') depth++;
                else if (c == ')' && depth > 0) depth--;
                else if (depth == 0 && (c == '.' || c == '!' || c == '?') && i + 1 < prefix.Length && Char.IsWhiteSpace(prefix[i + 1])) boundary = i + 1;
            }
            return boundary;
        }

        // A parenthetical is a pointer (a file name, path, section number or "see ..." reference) or review-stage
        // words alone; any other content is a statement of its own.
        private static bool IsAllowedParenthetical(string parenthetical)
        {
            string inner = parenthetical.Trim().TrimStart('(').TrimEnd(')').Trim();
            if (inner.Length == 0) return true;
            if (_ParentheticalReference.IsMatch(inner)) return !_DefectWord.IsMatch(inner);
            foreach (Match word in Regex.Matches(inner, @"[A-Za-z]+", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
                if (!_ParentheticalRoleWords.Contains(word.Value)) return false;
            return !Regex.IsMatch(inner, @"[0-9%]", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        }

        private static bool IsAllowedPrefix(string prefix)
        {
            string rest = Regex.Replace(prefix, @"\A(?:\s|>|[-*+_`#](?=\s|[A-Za-z])|\d+[.)](?=\s)|\[[ xX]\](?=\s))*", String.Empty,
                RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            rest = rest.Substring(LastSentenceBoundary(rest)).TrimStart();
            while (rest.Length > 0)
            {
                Match step = _AllowedLabel.Match(rest);
                if (!step.Success) step = _MetClause.Match(rest);
                if (step.Success && step.Length > 0)
                {
                    Match evidence = Regex.Match(step.Value, @"\([^)\n]*\)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
                    if (evidence.Success && !IsAllowedParenthetical(evidence.Value)) return false;
                }
                if (!step.Success) step = _PrefixConnective.Match(rest);
                if (!step.Success) step = Regex.Match(rest, @"\A[\s,;.—–*_`-]+", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
                if (!step.Success || step.Length == 0) return false;
                rest = rest.Substring(step.Length).TrimStart();
            }
            return true;
        }

        // The tail of a deferral is the rest of its line plus the following lines indented deeper than it; the next
        // criterion and the result marker end it first.
        private static int ItemTailEnd(string text, CriterionCandidate candidate, int end, int nextCriterion, int resultIndex)
        {
            int deferLineStart = candidate.PhraseIndex == 0 ? 0 : text.LastIndexOf('\n', candidate.PhraseIndex - 1) + 1;
            int indent = LeadingWhitespace(text, deferLineStart);
            int current = end >= text.Length ? text.Length : text.IndexOf('\n', end);
            if (current < 0) current = text.Length;
            while (current < text.Length)
            {
                int next = current + 1;
                int lineEnd = text.IndexOf('\n', next);
                if (lineEnd < 0) lineEnd = text.Length;
                string line = text.Substring(next, lineEnd - next);
                if (line.Trim().Length == 0 || LeadingWhitespace(text, next) <= indent) break;
                current = lineEnd;
            }
            foreach (int boundary in new[] { nextCriterion, resultIndex })
            {
                if (boundary < end || boundary >= current) continue;
                int boundaryLine = boundary == 0 ? 0 : text.LastIndexOf('\n', boundary - 1) + 1;
                if (boundaryLine > end) current = boundaryLine - 1;
            }
            return Math.Max(current, end);
        }

        private static int LeadingWhitespace(string text, int lineStart)
        {
            int count = 0;
            while (lineStart + count < text.Length && (text[lineStart + count] == ' ' || text[lineStart + count] == '\t')) count++;
            return count;
        }

        private static string NormalizeTail(string tail)
        {
            List<string> lines = new List<string>();
            foreach (string raw in tail.Split('\n'))
            {
                string line = Regex.Replace(raw, @"\A\s*(?:(?:[-*+>]|\d+[.)]|\[[ xX]\])\s+)*", String.Empty, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)).Trim();
                if (line.Length > 0) lines.Add(line);
            }
            return String.Join(" ", lines);
        }

        private static bool IsParentObjectiveOpen(string clause)
        {
            return Regex.IsMatch(clause, @"\A(?:The\s+)?parent\s+objective\s+(?:is|remains)\s+open[.!]?\s*\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        }

        // Every sentence after the statement's own must be a future-only clause the grammar accepts, the open
        // parent objective, or a fixed process statement.
        private static bool IsFutureOnlyTail(string tail, PendingReviewObligations obligations)
        {
            if (tail.Length == 0) return true;
            foreach (string clause in Regex.Split(tail, @"(?<=[.!?])\s+", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
            {
                if (IsFutureClause(clause, obligations) || IsParentObjectiveOpen(clause) || IsProcessStatement(clause)) continue;
                return false;
            }
            return true;
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
