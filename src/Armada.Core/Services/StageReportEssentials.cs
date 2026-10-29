namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;
    using System.Text.RegularExpressions;
    using Armada.Core.Models;

    /// <summary>
    /// The parts of a stage's report that the next stage must act on -- the reference to the complete
    /// output with its digest, the verdict, every blocking finding, the follow-ups and residuals, and the
    /// tests added -- kept whole within one bound. A handoff carries the report itself as a head-and-tail
    /// preview before a diff that can be far larger, and every size cap after it keeps the end of a brief,
    /// so the essentials sit at the end of the handoff block and survive those caps.
    /// </summary>
    public static class StageReportEssentials
    {
        #region Public-Members

        /// <summary>Heading that opens the essentials section of a handoff block.</summary>
        public const string Heading = "### Report essentials";

        /// <summary>Marker used when a bounded findings list does not contain every protected item.</summary>
        public const string IncompleteFindingsMarker = "[ARMADA: incomplete protected review findings]";

        /// <summary>Default bound for one essentials section.</summary>
        public const int DefaultMaxChars = 4000;

        #endregion

        #region Private-Members

        private static readonly string[] _BlockingMarkers =
        {
            "NOT DELIVERED",
            "NOT MET",
            "NOT RESOLVED",
            "**Blocking",
            "Blocking:",
            "BLOCKING:"
        };

        private static readonly Regex _NegatedBlocking = new Regex(
            @"\b(?:not\s+(?:a\s+)?|non-?\s?)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly string[] _KeptSectionKeywords =
        {
            "follow-up",
            "follow up",
            "residual",
            "tests added",
            "added tests",
            "new tests",
            "tests written"
        };

        private static readonly Regex _ParagraphDefect = new Regex(
            @"\brequired\s+(?:revision|change|correction)s?\b|\bincorrect(?:ly)?\b|\bcontradicts?\b|\bunsupported\b|\bunproven\b|\bnot\s+proven\b"
            + @"|\b(?:does|do)\s+not\s+(?:meet|establish|prove|satisfy)\b|\bstill\s+(?!pass)[a-z]+s\b"
            + @"|\bnot\s+(?:tested|covered)\b|\bno\s+tests?\s+(?:uses?|exercises?|drives?)\b|\bthe\s+gap\s+is\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // The line that introduces a list decides what its items are. A lead-in that names a defect
        // introduces findings; one that reports passing or reviewed behaviour introduces context; one
        // that labels minor or follow-up items introduces lesser items, which a rescue does not fix.
        private static readonly Regex _LesserLeadIn = new Regex(
            @"\b(?:minor|nits?|nitpicks?|follow-?ups?|non-?blocking|not\s+(?:a\s+)?blockers?|cosmetic|optional|lesser)\b"
            + @"|\A(?:remaining|pre-existing)\s+(?:inconsistencies|observations|notes)\s+(?:are|remain)\s+outside\s+(?:the\s+)?allowed\s+(?:edit\s+)?scope\z",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex _RequiredLeadIn = new Regex(
            @"\b(?:required|must|blocking)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex _DefectLeadIn = new Regex(
            @"\b(?:defects?|missing|gaps?|bugs?|wrong|broken|incorrect(?:ly)?|required|problems?|issues?|regressions?|not|never|lacks?|fails?(?!\s+closed))\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex _PassingLeadIn = new Regex(
            @"\b(?:sound|correct(?:ly)?|agree(?:s|d)?|pass(?:es|ed)?|exit(?:ed|s)?\s+0|cover(?:s|ed)?|fails?\s+closed|reviewed|checked|inspected|delivers?|match(?:es)?|acceptable|verified|holds?|fine)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // A lead-in that introduces the steps of a reproduction or counterexample: its items are evidence for
        // the finding they follow, not findings of their own.
        private static readonly Regex _EvidenceLeadIn = new Regex(
            @"\b(?:counter-?examples?|reproductions?|repros?|steps\s+to\s+reproduce|reproduc(?:e|ed)\s+(?:it|this|the\s+\w+)\s+(?:with|by|using)|interleavings?)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex _NonBlockingParenthetical = new Regex(
            @"\([^)]*\b(?:not\s+a\s+blocker|non-?blocking)\b[^)]*\)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // A closing sentence that is only a lesser label ("Follow-up.", "Follow-up, not a blocker.",
        // "Non-blocking.") marks the whole item as lesser, the same as a parenthetical label.
        private static readonly Regex _NonBlockingClosingLabel = new Regex(
            @"(?:^|[.!?]\s+)(?:a\s+)?(?:follow-?up|non-?blocking|not\s+a\s+blocker|minor|nit)"
            + @"(?:\s*[,;:]\s*(?:not\s+a\s+blocker|non-?blocking))?\s*[.!]?\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Case-sensitive on purpose: a directive opens a sentence with a capitalised verb.
        private static readonly Regex _ParagraphDirective = new Regex(
            @"(?:^|[.;!?]\s+)(?:Add|Replace|Correct|Fix|Resolve|Apply|Validate|Repair|Remove|Restore|Obtain|Prove|Preserve|Rerun|Re-run)\s",
            RegexOptions.Compiled);

        // A label that opens an item and marks it lesser: "Minor, not blocking:", "Observed, not blocking:",
        // "Not blocking, for the same rescue if cheap:".
        private static readonly Regex _LeadingLesserLabel = new Regex(
            @"^(?:(?:minor|observed|noted|cosmetic|optional)\b[^:.]{0,20}?[,\s]+(?:but\s+)?)?(?:not\s+(?:a\s+)?block(?:ing|er)|non-?blocking)\b"
            + @"|^(?:minor|nits?|cosmetic|optional)\s*:",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // A closing sentence that files the item as lesser in more words: "Recorded as a follow-up below.",
        // "Per the brief's rule this is a Suggested Follow-up, not a revision item.", "...; follow-up.",
        // "so this is a coverage note, not a defect."
        private static readonly Regex _LesserSentence = new Regex(
            @"\b(?:follow-?up|non-?blocking|not\s+(?:a\s+)?block(?:er|ing)|note\s+only|coverage\s+note|not\s+a\s+(?:defect|revision\s+item))\b|^(?:cosmetic|minor|harmless)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex _RequiredWording = new Regex(
            @"\b(?:required|must|needs?\s+to|should)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // The reviewer states outright that the item is not a defect.
        private static readonly Regex _DisclaimsDefect = new Regex(
            @"\bnot\s+a\s+defect\b|\bno\s+defect\b|\b(?:do|does|did)\s+not\s+count\s+(?:this|it)\s+as\s+a\s+defect\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // A verdict the reviewer gives the item itself; it stands unless the item qualifies it.
        private static readonly Regex _PassingItemVerdict = new Regex(
            @":\s*covered\b|\breviewed\s+and\s+tested\b|\bsource-exact\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Wording that reports agreement; it counts only when the item names no defect.
        private static readonly Regex _PassingItemWording = new Regex(
            @"\bmatch(?:es|ed)?\b|\b(?:is|are)\s+(?:otherwise\s+)?(?:sound|clean)\b|\bunchanged\b|\bunaffected\b|\btested\s+implicitly\b"
            + @"|\bis\s+what\s+(?:criterion|the\s+(?:brief|criterion))\b|\bno\s+uncovered\b|\bread-only\b|\bdoes\s+the\s+same\b|\breproduces?\b[^.]*\bexactly\b|\bis\s+correct\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex _DefectWord = new Regex(
            @"\b(?:defects?|missing|absent|gaps?|leaks?|wrong|bugs?|broken|incorrect(?:ly)?|regress\w*|diverg\w*|fails?(?!\s+closed)|failing|cannot|never|lacks?|undeclared|unproven|unsupported|contradicts?)\b"
            + @"|\b(?:does|do)\s+not\b|\bNOT\s+(?:MET|DELIVERED|RESOLVED)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex _QualifiedWording = new Regex(
            @"\b(?:but|however|except|although)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex _BoldLabel = new Regex(@"^\*\*([^*]+)\*\*", RegexOptions.Compiled);

        private static readonly Regex _DefectLabel = new Regex(
            @"\b(?:defects?|blocking|bugs?|required|regressions?|leaks?)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex _VerdictRequired = new Regex(
            @"\brequired\s+(?:revisions?|changes?|corrections?|fix(?:es)?)\b|\brevision\s+items?\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex _InlineNumberedItem = new Regex(@"\((\d+)\)\s", RegexOptions.Compiled);

        private const int _VerdictSectionChars = 600;
        private const int _KeptSectionChars = 900;

        #endregion

        #region Public-Methods

        /// <summary>
        /// Build the essentials section for one completed stage.
        /// </summary>
        /// <param name="persona">The stage's persona.</param>
        /// <param name="missionId">The stage's mission id.</param>
        /// <param name="agentOutput">The stage's redacted output; null when none was persisted.</param>
        /// <param name="artifact">The complete-output reference and digest.</param>
        /// <param name="maxChars">Bound for the whole section.</param>
        /// <returns>The section text, starting with <see cref="Heading"/>.</returns>
        public static string Build(string? persona, string missionId, string? agentOutput, MissionOutputArtifactPage artifact, int maxChars = DefaultMaxChars)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(Heading).Append(" (").Append(String.IsNullOrWhiteSpace(persona) ? "prior" : persona).Append(' ').Append(missionId).Append(")\n");
            sb.Append("Complete output: ").Append(artifact.ArtifactRef)
                .Append(" (").Append(artifact.TotalLength).Append(" chars, UTF-8 SHA-256 ").Append(artifact.Sha256).Append(")")
                .Append(". Read it with armada_mission_output before acting on anything this summary leaves out.\n");
            string reference = sb.ToString();
            const string incompleteHeading = "\nBlocking findings (incomplete; read armada_mission_output).\n";
            if (reference.Length + incompleteHeading.Length > maxChars)
            {
                reference = Heading + "\nOutput: " + artifact.ArtifactRef + "; SHA-256: " + artifact.Sha256
                    + "; read armada_mission_output.\n";
            }
            if (String.IsNullOrWhiteSpace(agentOutput)) return reference;

            string source = agentOutput.Replace("\r\n", "\n");
            List<string> parts = new List<string>();

            string blocking = BlockingFindings(source, Math.Max(400, maxChars / 2));
            if (blocking.Length > 0)
            {
                bool complete = !blocking.Contains(IncompleteFindingsMarker, StringComparison.Ordinal);
                parts.Add(complete
                    ? "Blocking findings (every one; fix all of them):\n" + blocking
                    : "Blocking findings (incomplete; read the complete output before acting):\n" + blocking);
            }

            string verdict = VerdictOf(source);
            if (verdict.Length > 0) parts.Add("Verdict:\n" + verdict);

            foreach (string section in KeptSections(source))
                parts.Add(section);

            StringBuilder body = new StringBuilder(reference);
            foreach (string part in parts)
            {
                int room = maxChars - body.Length - 2;
                bool blockingPart = part.StartsWith("Blocking findings (", StringComparison.Ordinal);
                if (blockingPart && part.Length > room)
                {
                    if (body.Length + incompleteHeading.Length <= maxChars) body.Append(incompleteHeading);
                    break;
                }
                if (room < 120)
                {
                    body.Append("(more of this report is in the complete output)\n");
                    break;
                }
                body.Append('\n').Append(part.Length <= room ? part : part.Substring(0, room).TrimEnd() + " ... (cut; see the complete output)").Append('\n');
            }
            return body.ToString();
        }

        /// <summary>
        /// Required items in Judge sections plus existing explicit NOT MET and Blocking items.
        /// Correctness, Failure Modes and Tests list items, labeled findings, and paragraphs that name a
        /// defect or ask for a fix are considered defects, except list items whose lead-in line reports
        /// passing, agreeing or reviewed behaviour (unless the item itself names a defect) and items under a minor
        /// or follow-up lead-in. Verdict carries required changes. Passing test summaries, MET criteria and
        /// explicitly non-blocking items are excluded. When the bound cannot hold every item, plain list
        /// items that name no defect are dropped before any other finding is shortened, and the result
        /// names the omission instead of claiming completeness.
        /// </summary>
        /// <param name="review">Review or report text.</param>
        /// <param name="maxChars">Bound for the list.</param>
        /// <returns>One finding per line, each starting with "- ".</returns>
        public static string BlockingFindings(string? review, int maxChars)
        {
            BlockingFindingSelection selection = SelectBlockingFindings(review, maxChars);
            if (selection.Items.Count == 0 && !selection.ExtractionMissed) return String.Empty;
            List<string> lines = selection.Items.Select(item => "- " + item).ToList();
            if (selection.Omitted > 0 || selection.ExtractionMissed) lines.Add(IncompleteFindingsLine(maxChars, selection.Omitted));
            return String.Join("\n", lines);
        }

        /// <summary>
        /// The blocking findings of a review that fit a character bound, each kept whole. A bound that cannot
        /// hold every finding drops whole findings, lowest priority first (plain list items that name no defect,
        /// then unmet acceptance criteria, then named defects, last in document order), and the selection counts
        /// what it dropped. A finding is never cut. Items come in document order, with unmet criteria after them.
        /// </summary>
        /// <param name="review">Review or report text.</param>
        /// <param name="maxChars">Bound for the rendered items, one character per line break included, before any omission line.</param>
        /// <returns>The kept findings, the count dropped, and whether a rejection yielded no finding at all.</returns>
        public static BlockingFindingSelection SelectBlockingFindings(string? review, int maxChars)
        {
            BlockingFindingSelection selection = new BlockingFindingSelection();
            if (String.IsNullOrWhiteSpace(review)) return selection;
            string text = review.Replace("\r\n", "\n");
            int firstSection = IndexOfFirstSectionHeader(text);
            if (firstSection > 0) text = text.Substring(firstSection);
            string[] lines = text.Split('\n');
            List<string> findings = new List<string>();
            List<string> criteria = new List<string>();
            HashSet<string> plainFindings = new HashSet<string>(StringComparer.Ordinal);
            string section = String.Empty;
            string leadIn = String.Empty;
            string paragraphLead = String.Empty;
            int protectedHeadingDepth = 0;
            bool inProposal = false;
            bool proposalHasClose = false;
            string priorSection = String.Empty;
            int priorHeadingDepth = 0;
            for (int i = 0; i < lines.Length; i++)
            {
                string raw = lines[i].Trim();
                if (String.Equals(raw, "[CLAUDE.MD-PROPOSAL]", StringComparison.OrdinalIgnoreCase))
                {
                    if (!inProposal)
                    {
                        priorSection = section;
                        priorHeadingDepth = protectedHeadingDepth;
                        proposalHasClose = lines.Skip(i + 1).Any(line => String.Equals(line.Trim(), "[/CLAUDE.MD-PROPOSAL]", StringComparison.OrdinalIgnoreCase));
                    }
                    inProposal = true;
                    section = String.Empty;
                    leadIn = String.Empty;
                    paragraphLead = String.Empty;
                    protectedHeadingDepth = 0;
                    continue;
                }
                if (String.Equals(raw, "[/CLAUDE.MD-PROPOSAL]", StringComparison.OrdinalIgnoreCase))
                {
                    if (inProposal)
                    {
                        section = priorSection;
                        protectedHeadingDepth = priorHeadingDepth;
                    }
                    inProposal = false;
                    leadIn = String.Empty;
                    paragraphLead = String.Empty;
                    continue;
                }
                if (inProposal && !proposalHasClose && raw.StartsWith("#", StringComparison.Ordinal)
                    && JudgeSection(raw).Length > 0
                    && (priorHeadingDepth == 0 || raw.TakeWhile(character => character == '#').Count() <= priorHeadingDepth))
                    inProposal = false;
                if (inProposal)
                {
                    section = String.Empty;
                    leadIn = String.Empty;
                    paragraphLead = String.Empty;
                }
                string boldSection = BoldLineSection(raw);
                if (boldSection.Length > 0)
                {
                    // A whole-line bold label such as "**Correctness**" opens a section the way a heading does.
                    // Any markdown heading after it closes it.
                    leadIn = String.Empty;
                    paragraphLead = String.Empty;
                    section = boldSection;
                    protectedHeadingDepth = _BoldSectionDepth;
                    continue;
                }
                if (raw.StartsWith("#", StringComparison.Ordinal))
                {
                    leadIn = String.Empty;
                    paragraphLead = String.Empty;
                    int headingDepth = raw.TakeWhile(character => character == '#').Count();
                    string recognizedSection = JudgeSection(raw);
                    if (recognizedSection.Length > 0)
                    {
                        section = recognizedSection;
                        protectedHeadingDepth = headingDepth;
                    }
                    else if (section.Length == 0 || headingDepth <= protectedHeadingDepth)
                    {
                        section = String.Empty;
                        protectedHeadingDepth = 0;
                    }
                    continue;
                }
                if (raw.Length == 0) continue;

                string item;
                bool listItem = TryStripListPrefix(raw, out item);
                if (listItem)
                {
                    // Classify the complete result, including an indented command or exit status.
                    while (i + 1 < lines.Length && IsContinuation(lines[i + 1]))
                    {
                        string continuation = lines[i + 1].Trim();
                        if (IsFindingLabel(continuation) || IsDefectLabel(continuation) || IsExplicitBlocking(continuation)
                            || _ParagraphDirective.IsMatch(continuation) || _ParagraphDefect.IsMatch(continuation)) break;
                        i++;
                        item += " " + continuation;
                        raw += " " + continuation;
                    }
                }
                bool leadInLine = !listItem && item.EndsWith(":", StringComparison.Ordinal);
                if (!listItem) leadIn = item;
                if (leadInLine) paragraphLead = item;
                // A lead-in that introduces a list covers that list; the paragraph after the list stands alone.
                if (listItem) paragraphLead = String.Empty;
                if (section == "Verdict" && (item.StartsWith("[ARMADA:", StringComparison.Ordinal) || leadInLine)) continue;
                bool acceptanceSection = section == "Completeness" || section == "Acceptance Criteria" || section == "Definition of Done";
                if (IsExplicitlyNonBlocking(raw, item)) continue;
                if (IsPositiveNoTestResult(item, section)) continue;
                if (acceptanceSection && IsCompletedCriterion(raw)) continue;
                bool notMetCriterion = ContainsAny(raw, "NOT DELIVERED", "NOT MET", "NOT RESOLVED");

                bool boldDefect = IsDefectLabel(item);
                bool explicitBlocking = IsExplicitBlocking(raw) || boldDefect;
                if (!notMetCriterion && !explicitBlocking && !IsFindingLabel(item)
                    && (IsExplicitSuccess(item, section) || IsSourceCompletenessSuccess(item, leadIn))) continue;
                if (!notMetCriterion && !explicitBlocking && IsLesserItem(item)) continue;
                bool findingSectionItem = section == "Correctness"
                    || section == "Failure Modes"
                    || section == "Tests"
                    || section == "Evidence"
                    || section == "Residual Risks";
                bool labeledFinding = findingSectionItem && IsFindingLabel(item);
                bool paragraphFinding = !listItem && IsParagraphFinding(item, section);
                bool itemNamesDefect = findingSectionItem && (_ParagraphDefect.IsMatch(item) || _ParagraphDirective.IsMatch(item) || IsUnmarkedTestGap(item, section));
                // Only the closing clause of a lead-in introduces the list: "... all match the source. Specific
                // traces:" introduces neutral traces, not passing ones.
                string leadClause = LastSentence(leadIn);
                bool underLead = listItem && findingSectionItem && leadIn.Length > 0;
                bool lesserLead = underLead && _LesserLeadIn.IsMatch(leadClause) && !_RequiredLeadIn.IsMatch(leadClause);
                bool defectLead = underLead && !lesserLead && _DefectLeadIn.IsMatch(leadClause);
                bool passingLead = underLead && !lesserLead && !defectLead && _PassingLeadIn.IsMatch(leadClause);
                bool evidenceLead = underLead && !lesserLead && _EvidenceLeadIn.IsMatch(leadClause) && !_RequiredLeadIn.IsMatch(leadClause);
                if (lesserLead && !explicitBlocking && !notMetCriterion) continue;

                // A paragraph that follows a lead-in naming defects ("Confirmed defect:") is one of those defects.
                string paragraphClause = LastSentence(paragraphLead);
                bool underDefectParagraphLead = !listItem && !leadInLine && findingSectionItem && paragraphLead.Length > 0
                    && _DefectLeadIn.IsMatch(paragraphClause)
                    && !(_LesserLeadIn.IsMatch(paragraphClause) && !_RequiredLeadIn.IsMatch(paragraphClause));
                if (!notMetCriterion && !explicitBlocking && !labeledFinding && !underDefectParagraphLead
                    && findingSectionItem && IsPassingItem(item)) continue;

                bool verdictRequired = section == "Verdict" && !listItem && _VerdictRequired.IsMatch(item);
                if (verdictRequired && !notMetCriterion)
                {
                    List<string> inlineItems = InlineNumberedItems(item);
                    if (inlineItems.Count >= 2)
                    {
                        foreach (string inlineItem in inlineItems)
                        {
                            if (!findings.Contains(inlineItem, StringComparer.Ordinal)) findings.Add(inlineItem);
                        }
                        continue;
                    }
                }

                bool listFinding = listItem && (!(passingLead || evidenceLead) || itemNamesDefect);
                bool sectionFinding = findingSectionItem && (listFinding || labeledFinding || paragraphFinding || underDefectParagraphLead || IsUnmarkedTestGap(item, section))
                    || section == "Verdict"
                        && (verdictRequired || ContainsAny(raw, "NEEDS_REVISION", "REQUIRED CHANGE", "MUST ") || (listItem && !ContainsAny(raw, "PASS")));
                if (!notMetCriterion && !explicitBlocking && !sectionFinding) continue;

                StringBuilder finding = new StringBuilder(item);
                while (i + 1 < lines.Length && IsContinuation(lines[i + 1]))
                {
                    i++;
                    finding.Append(' ').Append(lines[i].Trim());
                }
                if (labeledFinding)
                {
                    while (i + 1 < lines.Length)
                    {
                        string continuation = lines[i + 1].Trim();
                        if (continuation.Length == 0)
                        {
                            i++;
                            continue;
                        }
                        if (continuation.StartsWith("#", StringComparison.Ordinal)
                            || IsExplicitlyNonBlocking(continuation, continuation)
                            || IsFindingLabel(continuation)
                            || TryStripListPrefix(continuation, out _))
                        {
                            break;
                        }
                        i++;
                        finding.Append(' ').Append(continuation);
                    }
                }
                string value = finding.ToString().Trim();
                List<string> destination = notMetCriterion ? criteria : findings;
                if (!destination.Contains(value, StringComparer.Ordinal)) destination.Add(value);
                bool plain = !notMetCriterion && !explicitBlocking && !labeledFinding && !paragraphFinding
                    && !underDefectParagraphLead && !itemNamesDefect && !defectLead && section != "Verdict";
                if (plain) plainFindings.Add(value);
            }

            if (findings.Count == 0 && criteria.Count == 0)
            {
                // A rejection that yields no finding means the extraction missed them, not that there are none.
                selection.ExtractionMissed = IsRejection(text);
                return selection;
            }

            int total = findings.Concat(criteria).Sum(item => item.Length + 2) + Math.Max(0, findings.Count + criteria.Count - 1);
            if (total <= maxChars)
            {
                selection.Items.AddRange(findings);
                selection.Items.AddRange(criteria);
                return selection;
            }

            // Priority order for keeping: named defects, then unmet criteria, then plain list items. The kept set
            // is the longest prefix of that order that fits beside the omission line; a finding is kept whole or
            // dropped whole.
            List<string> byPriority = new List<string>();
            byPriority.AddRange(findings.Where(item => !plainFindings.Contains(item)));
            byPriority.AddRange(criteria);
            byPriority.AddRange(findings.Where(item => plainFindings.Contains(item)));
            int room = maxChars - IncompleteFindingsLine(maxChars, byPriority.Count).Length - 1;
            HashSet<string> kept = new HashSet<string>(StringComparer.Ordinal);
            int used = 0;
            foreach (string item in byPriority)
            {
                int added = item.Length + 2 + (kept.Count > 0 ? 1 : 0);
                if (used + added > room) break;
                used += added;
                kept.Add(item);
            }
            selection.Items.AddRange(findings.Where(kept.Contains));
            selection.Items.AddRange(criteria.Where(kept.Contains));
            selection.Omitted = byPriority.Count - selection.Items.Count;
            return selection;
        }

        /// <summary>
        /// The last essentials section in a text, through the end of its body; empty when there is none.
        /// </summary>
        /// <param name="text">A handoff block or a whole description.</param>
        /// <returns>The section text.</returns>
        public static string ExtractLast(string? text)
        {
            if (String.IsNullOrEmpty(text)) return String.Empty;
            int start = text.LastIndexOf(Heading, StringComparison.Ordinal);
            if (start < 0) return String.Empty;
            int end = text.Length;
            foreach (string stop in new[] { "\n---\n", "\n# ", "\n## ", "\n### " })
            {
                int at = text.IndexOf(stop, start + Heading.Length, StringComparison.Ordinal);
                if (at >= 0 && at < end) end = at;
            }
            return text.Substring(start, end - start).TrimEnd() + "\n";
        }

        #endregion

        #region Private-Methods

        private static string VerdictOf(string source)
        {
            List<string> parts = new List<string>();
            string section = SectionBody(source, header => header.Contains("verdict", StringComparison.OrdinalIgnoreCase), _VerdictSectionChars);
            if (section.Length > 0) parts.Add(section);
            string? marker = source.Split('\n').LastOrDefault(line => line.Contains("[ARMADA:VERDICT]", StringComparison.Ordinal));
            if (!String.IsNullOrWhiteSpace(marker) && !section.Contains(marker.Trim(), StringComparison.Ordinal)) parts.Add(marker.Trim());
            return String.Join("\n", parts);
        }

        private static IEnumerable<string> KeptSections(string source)
        {
            string[] lines = source.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].TrimStart();
                if (!line.StartsWith("#", StringComparison.Ordinal)) continue;
                string header = line.TrimStart('#').Trim();
                if (!_KeptSectionKeywords.Any(keyword => header.Contains(keyword, StringComparison.OrdinalIgnoreCase))) continue;
                StringBuilder body = new StringBuilder(header).Append(":\n");
                int j = i + 1;
                for (; j < lines.Length && !lines[j].TrimStart().StartsWith("#", StringComparison.Ordinal); j++)
                {
                    if (lines[j].Trim().Length > 0) body.Append(lines[j].TrimEnd()).Append('\n');
                }
                string text = body.ToString().TrimEnd();
                yield return text.Length <= _KeptSectionChars ? text : text.Substring(0, _KeptSectionChars).TrimEnd() + " ... (cut; see the complete output)";
                i = j - 1;
            }
        }

        private static string SectionBody(string source, Func<string, bool> matches, int maxChars)
        {
            string[] lines = source.Split('\n');
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                string line = lines[i].TrimStart();
                if (!line.StartsWith("#", StringComparison.Ordinal) || !matches(line.TrimStart('#').Trim())) continue;
                StringBuilder body = new StringBuilder();
                for (int j = i + 1; j < lines.Length && !lines[j].TrimStart().StartsWith("#", StringComparison.Ordinal); j++)
                {
                    if (lines[j].Trim().Length > 0) body.Append(lines[j].TrimEnd()).Append('\n');
                }
                string text = body.ToString().TrimEnd();
                return text.Length <= maxChars ? text : text.Substring(0, maxChars).TrimEnd() + " ...";
            }
            return String.Empty;
        }

        private static string JudgeSection(string heading)
        {
            string name = heading.TrimStart('#').Trim();
            if (name.StartsWith("Correctness", StringComparison.OrdinalIgnoreCase)) return "Correctness";
            // Linter and free-form reviewer reports name their defects under these headings.
            if (name.StartsWith("Code Correctness", StringComparison.OrdinalIgnoreCase)) return "Correctness";
            if (name.StartsWith("Required Changes", StringComparison.OrdinalIgnoreCase)) return "Correctness";
            if (name.StartsWith("Blocking Findings", StringComparison.OrdinalIgnoreCase)) return "Correctness";
            if (name.StartsWith("Blocking Issues", StringComparison.OrdinalIgnoreCase)) return "Correctness";
            if (name.StartsWith("Findings", StringComparison.OrdinalIgnoreCase)) return "Correctness";
            if (name.StartsWith("Failure Modes", StringComparison.OrdinalIgnoreCase)) return "Failure Modes";
            if (name.StartsWith("Tests", StringComparison.OrdinalIgnoreCase)) return "Tests";
            if (name.StartsWith("Evidence", StringComparison.OrdinalIgnoreCase)) return "Evidence";
            if (name.StartsWith("Residual Risks", StringComparison.OrdinalIgnoreCase)) return "Residual Risks";
            if (name.StartsWith("Verdict", StringComparison.OrdinalIgnoreCase)) return "Verdict";
            if (name.StartsWith("Completeness", StringComparison.OrdinalIgnoreCase)) return "Completeness";
            if (name.StartsWith("Acceptance Criteria", StringComparison.OrdinalIgnoreCase)) return "Acceptance Criteria";
            if (name.StartsWith("Definition of Done", StringComparison.OrdinalIgnoreCase)) return "Definition of Done";
            return String.Empty;
        }

        private const int _BoldSectionDepth = 7;

        private static readonly Regex _BoldLine = new Regex(@"^\*\*([^*]+?)\*\*:?$", RegexOptions.CultureInvariant);

        private static readonly Regex _VerdictMarker = new Regex(
            @"\[ARMADA:VERDICT\]\s*([A-Z_]+)", RegexOptions.CultureInvariant);

        // The final verdict marker decides; an earlier one may be quoted instruction text.
        private static bool IsRejection(string text)
        {
            MatchCollection matches = _VerdictMarker.Matches(text);
            if (matches.Count == 0) return false;
            string verdict = matches[matches.Count - 1].Groups[1].Value;
            return verdict == "NEEDS_REVISION" || verdict == "FAIL";
        }

        private static string BoldLineSection(string line)
        {
            Match match = _BoldLine.Match(line);
            if (!match.Success) return String.Empty;
            return JudgeSection(match.Groups[1].Value.Trim().TrimEnd(':'));
        }

        private static bool TryStripListPrefix(string source, out string item)
        {
            item = source.Trim();
            if (item.StartsWith("- ", StringComparison.Ordinal)
                || item.StartsWith("* ", StringComparison.Ordinal)
                || item.StartsWith("+ ", StringComparison.Ordinal))
            {
                item = item.Substring(2).TrimStart();
                return true;
            }
            int digits = 0;
            while (digits < item.Length && Char.IsDigit(item[digits])) digits++;
            if (digits > 0 && digits + 1 < item.Length
                && (item[digits] == '.' || item[digits] == ')')
                && Char.IsWhiteSpace(item[digits + 1]))
            {
                item = item.Substring(digits + 2).TrimStart();
                return true;
            }
            return false;
        }

        // The last sentence of a line, without its closing punctuation; file anchors such as "a.cs:12" do not split.
        private static string LastSentence(string text)
        {
            string trimmed = text.Trim().TrimEnd('.', '!', '?', ':', ' ');
            int cut = -1;
            foreach (string stop in new[] { ". ", "! ", "? ", "; " })
            {
                int at = trimmed.LastIndexOf(stop, StringComparison.Ordinal);
                if (at > cut) cut = at;
            }
            return cut < 0 ? trimmed : trimmed.Substring(cut + 2).Trim();
        }

        private static string BoldLabel(string item)
        {
            Match match = _BoldLabel.Match(item.Trim());
            return match.Success ? match.Groups[1].Value : String.Empty;
        }

        // A bold label that names a defect ("**Source fidelity defect (blocking).**") marks a finding.
        private static bool IsDefectLabel(string item)
        {
            string label = BoldLabel(item);
            return label.Length > 0 && _DefectLabel.IsMatch(label) && !IsLesserLabel(label) && !_DisclaimsDefect.IsMatch(label);
        }

        private static bool IsLesserLabel(string label)
        {
            string normalized = label.Trim().TrimStart('*', '_', '(').Trim();
            return _LeadingLesserLabel.IsMatch(normalized)
                || Regex.IsMatch(label, @"\b(?:non-?blocking|not\s+(?:a\s+)?block(?:ing|er)|minor|follow-?up)\b", RegexOptions.IgnoreCase)
                    && !Regex.IsMatch(label, @"\((?:blocking|required)\)", RegexOptions.IgnoreCase);
        }

        // An item the reviewer files as lesser: an opening lesser label, a lesser bold label, or a closing
        // sentence that calls it a follow-up, a note, or not a defect.
        private static bool IsLesserItem(string item)
        {
            string normalized = item.Trim().TrimStart('*', '_').Trim();
            if (_LeadingLesserLabel.IsMatch(normalized)) return true;
            string label = BoldLabel(item);
            if (label.Length > 0 && IsLesserLabel(label)) return true;
            string last = LastSentence(item);
            return _LesserSentence.IsMatch(last) && !_RequiredWording.IsMatch(last) && !_ParagraphDirective.IsMatch(last + " ");
        }

        // An item that reports agreement rather than a defect: the reviewer disclaims a defect, gives the item
        // a passing verdict without qualifying it, or reports agreement in an item that names no defect.
        private static bool IsPassingItem(string item)
        {
            if (_DisclaimsDefect.IsMatch(item)) return true;
            if (Regex.IsMatch(LastSentence(item), @"^(?:reviewed\s+and\s+tested|covered|source-exact)$", RegexOptions.IgnoreCase)) return true;
            if (_PassingItemVerdict.IsMatch(item)) return !_QualifiedWording.IsMatch(item) && !_DefectWord.IsMatch(item.Replace("fails closed", String.Empty, StringComparison.OrdinalIgnoreCase)) || !_QualifiedWording.IsMatch(item) && Regex.IsMatch(item, @":\s*covered\b", RegexOptions.IgnoreCase);
            return _PassingItemWording.IsMatch(item)
                && !_DefectWord.IsMatch(item)
                && !_RequiredWording.IsMatch(item)
                && !_ParagraphDirective.IsMatch(item);
        }

        // Required changes written inline in one Verdict paragraph: "(1) replace ...; (2) add ...".
        private static List<string> InlineNumberedItems(string paragraph)
        {
            List<string> items = new List<string>();
            MatchCollection markers = _InlineNumberedItem.Matches(paragraph);
            for (int m = 0; m < markers.Count; m++)
            {
                int start = markers[m].Index;
                int end = m + 1 < markers.Count ? markers[m + 1].Index : paragraph.Length;
                string text = paragraph.Substring(start, end - start).Trim().TrimEnd(';', ',').Trim();
                if (text.EndsWith(" and", StringComparison.Ordinal)) text = text.Substring(0, text.Length - 4).TrimEnd(';', ',', ' ');
                if (text.Length > markers[m].Length) items.Add(text);
            }
            return items;
        }

        private static bool IsExplicitlyNonBlocking(string line, string item)
        {
            return HasLabelPrefix(line, "Non-blocking")
                || HasLabelPrefix(item, "Non-blocking")
                || HasLabelPrefix(line, "Non blocking")
                || HasLabelPrefix(item, "Non blocking")
                || HasLabelPrefix(line, "Lesser item")
                || HasLabelPrefix(item, "Lesser item")
                || HasLabelPrefix(line, "Lower priority")
                || HasLabelPrefix(item, "Lower priority")
                || _NonBlockingParenthetical.IsMatch(item)
                || _NonBlockingClosingLabel.IsMatch(item.TrimEnd());
        }

        private static bool IsCompletedCriterion(string line)
        {
            MatchCollection statuses = Regex.Matches(
                line,
                @"(?:^|[—–])\s*(?<status>NOT\s+(?:MET|DELIVERED|RESOLVED)|MET|DELIVERED)\b",
                RegexOptions.IgnoreCase);
            if (statuses.Count > 0)
            {
                Match status = statuses[statuses.Count - 1];
                return !status.Groups["status"].Value.TrimStart().StartsWith("NOT ", StringComparison.OrdinalIgnoreCase);
            }
            if (ContainsAny(line, "NOT MET", "NOT DELIVERED", "NOT RESOLVED")) return false;
            return Regex.IsMatch(line, @"\bMET\b", RegexOptions.IgnoreCase)
                || Regex.IsMatch(line, @"\bDELIVERED\b", RegexOptions.IgnoreCase);
        }

        private static bool IsFindingLabel(string item)
        {
            string normalized = item.Trim().TrimStart('*', '_').TrimStart();
            return Regex.IsMatch(normalized, @"^(?:P[0-3]|R\d+)\b", RegexOptions.IgnoreCase);
        }

        private static bool IsUnmarkedTestGap(string item, string section)
        {
            return section == "Tests"
                && (ContainsAny(item, "COVERAGE MISSES", "COVERAGE MISSING", "MISSING COVERAGE", "NOT COVERED", "UNTESTED")
                    || Regex.IsMatch(item, @"\bno\s+tests?\s+(?:cover|covers|assert|asserts|for|exists?\s+for)\b", RegexOptions.IgnoreCase)
                    || Regex.IsMatch(item, @"\bno\s+assertions?\s+(?:for|covers?|exists?\s+for)\b", RegexOptions.IgnoreCase)
                    || HasDoesNotCoverFinding(item));
        }

        // A Judge often writes a defect as prose rather than a list item. A paragraph in Correctness,
        // Failure Modes or Tests is a finding when it names a defect or asks for a fix; narration of what
        // was reviewed, passing results and bare lead-in lines ending in a colon are not.
        private static bool IsParagraphFinding(string item, string section)
        {
            if (section != "Correctness" && section != "Failure Modes" && section != "Tests") return false;
            string text = item.Trim();
            if (text.Length == 0 || text.EndsWith(":", StringComparison.Ordinal)) return false;
            return _ParagraphDefect.IsMatch(text) || _ParagraphDirective.IsMatch(text);
        }

        private static bool HasDoesNotCoverFinding(string item)
        {
            if (Regex.IsMatch(item.Trim(), @"^no\s+tests?\s+(?:do|does)\s+not\s+cover\b", RegexOptions.IgnoreCase))
                return false;
            return Regex.IsMatch(item, @"\b(?:tests?\s+(?:do|does)\s+not\s+cover|does\s+not\s+cover)\b", RegexOptions.IgnoreCase);
        }

        private static bool IsPositiveNoTestResult(string item, string section)
        {
            string normalized = item.Trim().Trim('*', '_').Trim();
            if (section != "Tests") return false;
            if (IsFindingLabel(normalized)) return false;
            return Regex.IsMatch(normalized, @"^no\s+tests?\s+failed[.!]?$", RegexOptions.IgnoreCase)
                || Regex.IsMatch(normalized, @"^no\s+tests?\s+failed\s+to\s+(?:cover|assert)(?:\s+.+)?[.!]?$", RegexOptions.IgnoreCase)
                || Regex.IsMatch(normalized, @"^no\s+tests?\s+(?:do|does)\s+not\s+cover(?:\s+.+)?[.!]?$", RegexOptions.IgnoreCase)
                || IsPassingTestSummary(normalized);
        }

        // A count may carry thousands separators ("1,389 passed"). A tool summary may give only its issue count
        // ("Lint: 0 issues.").
        private static bool IsPassingTestSummary(string item)
        {
            const string count = @"(?:\d{1,3}(?:,\d{3})+|\d+)";
            return Regex.IsMatch(
                    item,
                    @"^[^:]+:\s*" + count + @"\s+passed,\s*0\s+failed(?:;\s*baseline\s+\d+,\s*with\s+(?:all\s+)?\d+\s+added\s+cases?\s+executed)?[.!]?$",
                    RegexOptions.IgnoreCase)
                || Regex.IsMatch(item, @"^[^:]+:\s*(?:" + count + @"\s+scripts?,\s*)?0\s+issues?[.!]?$", RegexOptions.IgnoreCase);
        }

        private static bool IsSourceCompletenessSuccess(string item, string leadIn)
        {
            const string location = @"[A-Za-z0-9_./\\-]*:\d+(?:-\d+)?";
            const string anchor = "(?:`" + location + "`|" + location + ")";
            return String.Equals(leadIn.Trim(), "Source completeness:", StringComparison.OrdinalIgnoreCase)
                && Regex.IsMatch(item, @"^[^:]+:\s*(?:preserved|(?:ported|reused|preserved)\s+(?:at|through)\s+"
                    + anchor + @"(?:\s+and\s+" + anchor + @")?)[.!]?$", RegexOptions.IgnoreCase)
                && !_ParagraphDefect.IsMatch(item)
                && !_QualifiedWording.IsMatch(item)
                && !_DefectWord.IsMatch(item)
                && !_RequiredWording.IsMatch(item)
                && !_ParagraphDirective.IsMatch(item);
        }

        private static bool IsSuccessfulTestResult(string item)
        {
            string plain = item.Replace("**", String.Empty, StringComparison.Ordinal);
            const string count = @"(?:\d{1,3}(?:,\d{3})+|\d+)";
            const string elapsed = @"(?:,\s*\d+(?:\.\d+)?\s+seconds)?";
            return Regex.IsMatch(plain,
                    @"^`[^`]+`:\s*(?:0\s+(?:failed|errors?)|" + count + @"\s+passed(?:,\s*0\s+failed)?" + elapsed + @")[.!]?$", RegexOptions.IgnoreCase)
                || Regex.IsMatch(plain, @"^Exact-commit Check\s+`[^`]+`:\s*passed" + elapsed + @"[.!]?$", RegexOptions.IgnoreCase)
                || Regex.IsMatch(plain,
                    @"^Foreground validation\b[^:]*:\s*`[^`]+`\s+Result:\s*exit(?:\s+code)?\s+0" + elapsed
                    + @"(?:,\s*admission\s+wait\s+0)?"
                    + @"(?:;\s*" + count + @"\s+(?:(?:cases|C#|Python|JavaScript)\s+)?passed(?:,\s*" + count + @"\s+skipped)?)*[.!]?$", RegexOptions.IgnoreCase);
        }

        private static bool IsExplicitSuccess(string item, string section)
        {
            if (_QualifiedWording.IsMatch(item)
                || Regex.IsMatch(item.Replace("**", String.Empty, StringComparison.Ordinal),
                    @"\b(?:exit(?:ed)?(?:\s+code)?\s+[1-9]\d*|[1-9]\d*\s+(?:failed|failures?|errors?))\b", RegexOptions.IgnoreCase))
                return false;
            if (section == "Tests" && IsSuccessfulTestResult(item)) return true;
            if (Regex.IsMatch(item, @"^Failure mode\b.*\bhandled(?:\s+by\b.+)?[.!]?$", RegexOptions.IgnoreCase))
                return !Regex.IsMatch(item, @"\b(not|never|unhandled|missing|still open|remains)\b", RegexOptions.IgnoreCase);
            if (ContainsAny(item, "MISSING", "FAIL", "FAILURE", "LACKS", "UNCOVERED", "NO TEST", "NO ASSERTION", "NO COVERAGE", "DOES NOT", "NOT RUN", "NOT COVER", "ABSENT", "UNTESTED"))
                return false;
            if (item.StartsWith("Verified ", StringComparison.OrdinalIgnoreCase)
                || item.StartsWith("Confirmed ", StringComparison.OrdinalIgnoreCase))
                return true;
            if (section == "Tests" && item.StartsWith("Passed ", StringComparison.OrdinalIgnoreCase))
                return true;
            if (section == "Tests" && Regex.IsMatch(item, @"\b(passed|verified|covers|covered|handled)\b(?:\s+(?:in|during|by)\b.*)?[.!]?$", RegexOptions.IgnoreCase))
                return true;
            return item.StartsWith("PASS:", StringComparison.OrdinalIgnoreCase)
                || item.StartsWith("PASSED:", StringComparison.OrdinalIgnoreCase)
                || item.StartsWith("VERIFIED:", StringComparison.OrdinalIgnoreCase)
                || item.StartsWith("SUCCESS:", StringComparison.OrdinalIgnoreCase)
                || item.StartsWith("NO DEFECT:", StringComparison.OrdinalIgnoreCase)
                || item.StartsWith("NON-ISSUE:", StringComparison.OrdinalIgnoreCase)
                || item.StartsWith("NO FAILURE:", StringComparison.OrdinalIgnoreCase)
                || item.StartsWith("NO ISSUE:", StringComparison.OrdinalIgnoreCase)
                || item.StartsWith("ALL TESTS PASSED", StringComparison.OrdinalIgnoreCase);
        }

        private static bool HasLabelPrefix(string line, string label)
        {
            string normalized = line.Trim().TrimStart('*', '_').TrimStart();
            if (!normalized.StartsWith(label, StringComparison.OrdinalIgnoreCase)) return false;
            string remainder = normalized.Substring(label.Length);
            if (remainder.Length == 0 || remainder[0] == ':' || Char.IsWhiteSpace(remainder[0])) return true;
            if (remainder[0] != '*' && remainder[0] != '_') return false;
            remainder = remainder.TrimStart('*', '_');
            return remainder.Length == 0 || remainder[0] == ':' || Char.IsWhiteSpace(remainder[0]);
        }

        private static bool IsExplicitBlocking(string line)
        {
            foreach (string marker in _BlockingMarkers)
            {
                int at = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                while (at >= 0)
                {
                    // "not blocking:" and "non-blocking:" carry the marker's text but file the item as lesser.
                    if (!_NegatedBlocking.IsMatch(line.Substring(0, at))) return true;
                    at = line.IndexOf(marker, at + marker.Length, StringComparison.OrdinalIgnoreCase);
                }
            }
            return false;
        }

        private static bool ContainsAny(string source, params string[] terms)
        {
            foreach (string term in terms)
            {
                if (source.Contains(term, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static string IncompleteFindingsLine(int maxChars, int omitted = 0)
        {
            string count = omitted > 0
                ? omitted + (omitted == 1 ? " lower-priority finding is" : " lower-priority findings are") + " omitted whole"
                : "Required items or details are omitted";
            string marker = "- " + IncompleteFindingsMarker + " " + count + "; read complete mission output with armada_mission_output before editing.";
            if (marker.Length <= maxChars) return marker;
            return "- " + IncompleteFindingsMarker;
        }

        private static int IndexOfFirstSectionHeader(string source)
        {
            if (source.StartsWith("## ", StringComparison.Ordinal)) return 0;
            int index = source.IndexOf("\n## ", StringComparison.Ordinal);
            return index < 0 ? -1 : index + 1;
        }

        // A wrapped line of the same finding: indented, not blank, not a new list item or header.
        private static bool IsContinuation(string line)
        {
            if (String.IsNullOrWhiteSpace(line)) return false;
            if (!Char.IsWhiteSpace(line[0])) return false;
            string trimmed = line.TrimStart();
            if (trimmed.StartsWith("#", StringComparison.Ordinal)) return false;
            if (trimmed.StartsWith("- ", StringComparison.Ordinal) || trimmed.StartsWith("* ", StringComparison.Ordinal)) return false;
            int digits = 0;
            while (digits < trimmed.Length && Char.IsDigit(trimmed[digits])) digits++;
            return !(digits > 0 && digits < trimmed.Length && trimmed[digits] == '.');
        }

        #endregion
    }
}
