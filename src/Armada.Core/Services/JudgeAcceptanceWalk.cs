namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Text.RegularExpressions;

    /// <summary>
    /// The Judge must walk every acceptance criterion from the brief. A PASS that omits the walk,
    /// or that names a criterion NOT MET, is refused. The walk is a real ground: review_substance
    /// never overturns it.
    /// </summary>
    public static class JudgeAcceptanceWalk
    {
        /// <summary>Section heading the brief and the Judge share for the walk.</summary>
        public const string SectionName = "Acceptance Criteria";

        /// <summary>Failure reason when a PASS marks a brief criterion NOT MET. This is a substantive rejection, never a format miss.</summary>
        public const string NotMetReason = "Judge PASS names a NOT MET acceptance criterion";

        private static readonly Regex _NotMet = new Regex(@"\bNOT\s+MET\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex _Met = new Regex(@"\b(?:NOT\s+)?MET\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex _ObjectiveBriefStart = new Regex(
            @"^<!-- armada-objective-brief:[^>\r\n]+ -->\r?$",
            RegexOptions.Multiline | RegexOptions.CultureInvariant);
        private static readonly Regex _ObjectiveBriefEnd = new Regex(
            @"^<!-- /armada-objective-brief -->\r?$",
            RegexOptions.Multiline | RegexOptions.CultureInvariant);
        private static readonly Regex _HandoffMarker = new Regex(
            @"^<!-- ARMADA:HANDOFF:[^>\r\n]+ -->\r?$",
            RegexOptions.Multiline | RegexOptions.CultureInvariant);

        /// <summary>Bullet items under the brief's Acceptance Criteria heading.</summary>
        public static List<string> ExtractCriteria(string? brief)
        {
            if (String.IsNullOrWhiteSpace(brief)) return new List<string>();

            Match handoff = _HandoffMarker.Match(brief);
            int baseLimit = handoff.Success ? handoff.Index : brief.Length;
            string baseDescription = brief.Substring(0, baseLimit);
            if (TryFindLastCompleteObjectiveFrame(baseDescription, out Match objectiveStart, out int objectiveEnd))
            {
                string prefix = StripObjectiveFrames(baseDescription.Substring(0, objectiveStart.Index));
                List<string> items = ReadFirstCriteriaSection(prefix);
                items.AddRange(ReadFirstCriteriaSection(baseDescription.Substring(objectiveStart.Index, objectiveEnd - objectiveStart.Index)));
                return Deduplicate(items);
            }

            // Legacy descriptions and incomplete bounded frames keep the first-section rule.
            return ReadFirstCriteriaSection(baseDescription);
        }

        /// <summary>Heading of the versioned Architect plan's per-block acceptance items.</summary>
        public const string EvidenceContractSectionName = "Acceptance evidence contract";

        private static readonly Regex _SliceAcceptanceLabel = new Regex(
            @"^\*\*Acceptance:?\*\*:?\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex _BoldLabelLine = new Regex(
            @"^\*\*.+\*\*:?\s*$", RegexOptions.CultureInvariant);

        /// <summary>
        /// The acceptance criteria an Architect plan block declares for itself, read from the block text. The
        /// forms are the ones the Architect plan defines: the bulleted list under the <c>**Acceptance:**</c> label,
        /// the bullets under an <c>## Acceptance Criteria</c> heading, and the items of the versioned
        /// <c>## Acceptance evidence contract</c> (the item's first line; indented continuation lines are not
        /// criteria). Objective frames and handoff blocks are ignored.
        /// </summary>
        /// <param name="blockText">The description of the plan block's first stage.</param>
        /// <returns>The block-local criteria; empty when the text declares none in a recognized form.</returns>
        public static List<string> ExtractSliceBlockCriteria(string? blockText)
        {
            if (String.IsNullOrWhiteSpace(blockText)) return new List<string>();

            Match handoff = _HandoffMarker.Match(blockText);
            int baseLimit = handoff.Success ? handoff.Index : blockText.Length;
            string baseText = StripObjectiveFrames(blockText.Substring(0, baseLimit));

            List<string> items = ReadLabelBullets(baseText);
            items.AddRange(ReadFirstCriteriaSection(baseText));
            items.AddRange(ReadTopLevelBullets(ExtractSection(baseText, EvidenceContractSectionName)));
            return Deduplicate(items);
        }

        private static List<string> ReadTopLevelBullets(string? block)
        {
            List<string> items = new List<string>();
            if (String.IsNullOrWhiteSpace(block)) return items;
            foreach (string raw in block.Replace("\r\n", "\n").Split('\n'))
            {
                if (!(raw.StartsWith("- ", StringComparison.Ordinal) || raw.StartsWith("* ", StringComparison.Ordinal))) continue;
                string item = Regex.Replace(raw.Substring(2).Trim(), @"\s+", " ");
                if (item.Length > 0) items.Add(item);
            }
            return items;
        }

        private static List<string> ReadLabelBullets(string text)
        {
            List<string> items = new List<string>();
            string[] lines = text.Replace("\r\n", "\n").Split('\n');
            for (int index = 0; index < lines.Length; index++)
            {
                if (!_SliceAcceptanceLabel.IsMatch(lines[index].Trim())) continue;
                int labelIndent = lines[index].Length - lines[index].TrimStart().Length;
                for (int next = index + 1; next < lines.Length; next++)
                {
                    string line = lines[next];
                    string trimmed = line.Trim();
                    if (trimmed.Length == 0) continue;
                    int indent = line.Length - line.TrimStart().Length;
                    if (trimmed.StartsWith("#", StringComparison.Ordinal) || _BoldLabelLine.IsMatch(trimmed)) break;
                    bool bullet = trimmed.StartsWith("- ", StringComparison.Ordinal) || trimmed.StartsWith("* ", StringComparison.Ordinal);
                    if (indent > labelIndent) continue;
                    if (!bullet) break;
                    string item = Regex.Replace(trimmed.Substring(2).Trim(), @"\s+", " ");
                    if (item.Length > 0) items.Add(item);
                }
            }
            return items;
        }

        internal static bool HasCompleteObjectiveBrief(string? description, string renderedBrief)
        {
            if (String.IsNullOrWhiteSpace(description) || String.IsNullOrWhiteSpace(renderedBrief)) return false;
            Match handoff = _HandoffMarker.Match(description);
            string baseDescription = handoff.Success ? description.Substring(0, handoff.Index) : description;
            if (!TryFindLastCompleteObjectiveFrame(baseDescription, out Match start, out int end)) return false;
            Match closing = _ObjectiveBriefEnd.Match(baseDescription, end);
            if (!closing.Success || closing.Index != end) return false;

            string completeFrame = baseDescription.Substring(start.Index, closing.Index + closing.Length - start.Index);
            completeFrame = NormalizeLineEndings(completeFrame.TrimEnd('\r'));
            string expectedFrame = NormalizeLineEndings(renderedBrief.TrimEnd());
            return String.Equals(completeFrame, expectedFrame, StringComparison.Ordinal);
        }

        private static string NormalizeLineEndings(string value)
        {
            return value.Replace("\r\n", "\n").Replace('\r', '\n');
        }

        private static bool TryFindLastCompleteObjectiveFrame(string text, out Match start, out int end)
        {
            start = Match.Empty;
            end = -1;
            MatchCollection starts = _ObjectiveBriefStart.Matches(text);
            for (int index = 0; index < starts.Count; index++)
            {
                Match candidate = starts[index];
                int nextStart = index + 1 < starts.Count ? starts[index + 1].Index : text.Length;
                Match candidateEnd = _ObjectiveBriefEnd.Match(text, candidate.Index + candidate.Length);
                if (!candidateEnd.Success || candidateEnd.Index >= nextStart) continue;
                start = candidate;
                end = candidateEnd.Index;
            }
            return start.Success;
        }

        private static string StripObjectiveFrames(string text)
        {
            MatchCollection starts = _ObjectiveBriefStart.Matches(text);
            if (starts.Count == 0) return text;

            System.Text.StringBuilder clean = new System.Text.StringBuilder(text.Length);
            int cursor = 0;
            for (int index = 0; index < starts.Count; index++)
            {
                Match start = starts[index];
                if (start.Index < cursor) continue;
                int nextStart = index + 1 < starts.Count ? starts[index + 1].Index : text.Length;
                Match end = _ObjectiveBriefEnd.Match(text, start.Index + start.Length);
                int frameEnd = end.Success && end.Index < nextStart ? end.Index + end.Length : nextStart;
                clean.Append(text, cursor, start.Index - cursor);
                cursor = frameEnd;
            }
            clean.Append(text, cursor, text.Length - cursor);
            return clean.ToString();
        }

        private static List<string> ReadFirstCriteriaSection(string? text)
        {
            List<string> items = new List<string>();
            string? block = ExtractSection(text, SectionName);
            if (String.IsNullOrWhiteSpace(block)) return items;
            foreach (string raw in block.Replace("\r\n", "\n").Split('\n'))
            {
                string line = raw.Trim();
                if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal))
                {
                    string item = Regex.Replace(line.Substring(2).Trim(), @"\s+", " ");
                    if (item.Length > 0 && !item.StartsWith("[additional", StringComparison.OrdinalIgnoreCase))
                        items.Add(item);
                }
            }
            return items;
        }

        private static List<string> Deduplicate(IEnumerable<string> items)
        {
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            List<string> unique = new List<string>();
            foreach (string item in items)
            {
                string normalized = Regex.Replace(item.Trim(), @"\s+", " ");
                if (seen.Add(normalized)) unique.Add(normalized);
            }
            return unique;
        }

        /// <summary>
        /// A compact criteria block for brief pinning: heading plus bullets only.
        /// Null when the brief lists no criteria.
        /// </summary>
        public static string? FormatPinnedBlock(string? brief)
        {
            List<string> items = ExtractCriteria(brief);
            if (items.Count == 0) return null;
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.Append("## ").Append(SectionName).Append("\n");
            foreach (string item in items)
                sb.Append("- ").Append(item).Append("\n");
            return sb.ToString();
        }

        /// <summary>Require a distinct, evidenced MET entry for each criterion in the brief.</summary>
        public static string? ValidatePass(string? agentOutput, string? brief)
        {
            return ValidatePass(agentOutput, brief, null);
        }

        /// <summary>
        /// Require a distinct, evidenced MET entry for each CURRENT acceptance criterion. When
        /// <paramref name="currentCriteria"/> is non-null it is the authoritative expected set — the
        /// linked objective's live criteria, which an operator may narrow after dispatch — and the
        /// <paramref name="brief"/> snapshot is not read for the expected list, so a PASS that covers
        /// the current criteria is not refused on criteria the dispatch-time brief still lists. When it
        /// is null the expected set comes from the brief, for a mission with no linked objective.
        /// </summary>
        /// <param name="agentOutput">The Judge report.</param>
        /// <param name="brief">The mission description the Judge was given.</param>
        /// <param name="currentCriteria">The live criteria to measure, or null to read the brief.</param>
        /// <returns>The failure reason, or null when the walk is satisfied.</returns>
        public static string? ValidatePass(string? agentOutput, string? brief, IReadOnlyList<string>? currentCriteria)
        {
            // A criterion listed twice is one criterion: one MET line covers it. The current criteria win
            // over the brief snapshot when supplied, so the walk measures what the objective requires now.
            List<string> expected;
            if (currentCriteria != null)
            {
                List<string> nonEmpty = new List<string>();
                foreach (string criterion in currentCriteria)
                {
                    if (!String.IsNullOrWhiteSpace(criterion)) nonEmpty.Add(criterion);
                }
                expected = Deduplicate(nonEmpty);
            }
            else
            {
                expected = Deduplicate(ExtractCriteria(brief));
            }
            if (expected.Count == 0) return null;

            // The walk is required exactly when a criterion is expected; read the section from the output
            // against the resolved expected set, not the brief.
            if (String.IsNullOrWhiteSpace(ExtractSection(agentOutput, SectionName)))
                return "Judge PASS verdict missing required review sections: " + SectionName;
            string block = ExtractSection(agentOutput, SectionName)!;
            // Criteria that read the same once redacted are one criterion, as the stored output cannot tell them apart.
            List<string> expectedClaims = new List<string>();
            HashSet<string> seenClaims = new HashSet<string>(StringComparer.Ordinal);
            foreach (string criterion in expected)
            {
                string comparable = ComparableClaim(criterion);
                if (seenClaims.Add(comparable)) expectedClaims.Add(comparable);
            }
            HashSet<int> covered = new HashSet<int>();
            foreach (string raw in block.Replace("\r\n", "\n").Split('\n'))
            {
                string line = raw.Trim();
                MatchCollection marks = _Met.Matches(line);
                foreach (Match mark in marks)
                {
                    string claim = ComparableClaim(line.Substring(0, mark.Index));
                    string evidence = line.Substring(mark.Index + mark.Length);
                    for (int index = 0; index < expectedClaims.Count; index++)
                    {
                        if (!String.Equals(claim, expectedClaims[index], StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (_NotMet.IsMatch(mark.Value))
                            return NotMetReason;
                        if (covered.Contains(index)) break;
                        if (!HasEvidence(evidence))
                            return "Judge PASS acceptance criterion lacks file:line or command evidence";
                        covered.Add(index);
                        break;
                    }
                }
            }

            return covered.Count == expectedClaims.Count
                ? null
                : "Judge PASS does not list every acceptance criterion from the brief";
        }

        /// <summary>
        /// Whether a Judge report lacks the walk the brief asks for: the brief lists criteria and the
        /// report has no non-empty <c>## Acceptance Criteria</c> section.
        /// </summary>
        /// <param name="agentOutput">The Judge report.</param>
        /// <param name="brief">The mission description the Judge was given.</param>
        /// <returns>True when the walk is required and absent.</returns>
        public static bool LacksWalkSection(string? agentOutput, string? brief)
        {
            if (ExtractCriteria(brief).Count == 0) return false;
            return String.IsNullOrWhiteSpace(ExtractSection(agentOutput, SectionName));
        }

        /// <summary>
        /// The form in which a Judge's criterion claim and a brief criterion are compared. A Judge's output is
        /// stored through <see cref="SecretRedactor.Redact(string?)"/>, so a criterion whose text holds a
        /// secret-shaped value reaches the check with that value replaced. Both sides pass through the same
        /// redactor before they are compared, so a criterion the Judge copied exactly still matches its brief
        /// text, and the brief's unredacted value is never compared with stored output.
        /// </summary>
        private static string ComparableClaim(string value)
        {
            return NormalizeClaim(SecretRedactor.Redact(value));
        }

        private static string NormalizeClaim(string value)
        {
            string claim = Regex.Replace(value.Trim(), @"^(?:[-*]|\d+[.)])\s+", String.Empty);
            claim = claim.Replace("**", String.Empty).Trim().TrimEnd(':', '-', ' ', '.', '—');
            return Regex.Replace(claim, @"\s+", " ");
        }

        private static bool HasEvidence(string text)
        {
            // Check the evidence shape only. The Judge remains responsible for verifying its content.
            return Regex.IsMatch(text, @"[\w./\-]+:[1-9]\d*\b", RegexOptions.CultureInvariant)
                || Regex.IsMatch(text, @"\bcommand\s*:\s*`[^`\r\n]+`", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        /// <summary>The markdown section body for a heading, or null when absent.</summary>
        public static string? ExtractSection(string? text, string heading)
        {
            if (String.IsNullOrWhiteSpace(text) || String.IsNullOrWhiteSpace(heading)) return null;
            string needle = "## " + heading;
            int start = IndexOfHeading(text, needle);
            if (start < 0) return null;
            int body = text.IndexOf('\n', start);
            if (body < 0) return String.Empty;
            body++;
            int end = text.Length;
            int cursor = body;
            while (cursor < text.Length)
            {
                int lineEnd = text.IndexOf('\n', cursor);
                if (lineEnd < 0) lineEnd = text.Length;
                string line = text.Substring(cursor, lineEnd - cursor).TrimStart();
                if (line.StartsWith("## ", StringComparison.Ordinal) && !line.StartsWith(needle, StringComparison.OrdinalIgnoreCase))
                {
                    end = cursor;
                    break;
                }
                if (line.StartsWith("---", StringComparison.Ordinal) || line.StartsWith("...(mission", StringComparison.Ordinal))
                {
                    end = cursor;
                    break;
                }
                cursor = lineEnd < text.Length ? lineEnd + 1 : text.Length;
            }
            return text.Substring(body, end - body).Trim();
        }

        private static int IndexOfHeading(string text, string needle)
        {
            int from = 0;
            while (from < text.Length)
            {
                int at = text.IndexOf(needle, from, StringComparison.OrdinalIgnoreCase);
                if (at < 0) return -1;
                int end = text.IndexOf('\n', at);
                if (end < 0) end = text.Length;
                if ((at == 0 || text[at - 1] == '\n')
                    && String.Equals(text.Substring(at, end - at).TrimEnd(), needle, StringComparison.OrdinalIgnoreCase)) return at;
                from = at + 1;
            }
            return -1;
        }
    }
}
