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

        private static readonly Regex _NotMet = new Regex(@"\bNOT\s+MET\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex _Met = new Regex(@"\bMET\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
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
            List<string> expected = ExtractCriteria(brief);
            if (expected.Count == 0) return null;

            string? block = ExtractSection(agentOutput, SectionName);
            if (String.IsNullOrWhiteSpace(block))
                return "Judge PASS verdict missing required review sections: " + SectionName;
            if (_NotMet.IsMatch(block))
                return "Judge PASS names a NOT MET acceptance criterion";

            HashSet<int> covered = new HashSet<int>();
            foreach (string raw in block.Replace("\r\n", "\n").Split('\n'))
            {
                string line = raw.Trim();
                MatchCollection marks = _Met.Matches(line);
                foreach (Match mark in marks)
                {
                    string claim = NormalizeClaim(line.Substring(0, mark.Index));
                    string evidence = line.Substring(mark.Index + mark.Length);
                    for (int index = 0; index < expected.Count; index++)
                    {
                        if (covered.Contains(index) || !String.Equals(claim, NormalizeClaim(expected[index]), StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (!HasEvidence(evidence))
                            return "Judge PASS acceptance criterion lacks file:line or command evidence";
                        covered.Add(index);
                        break;
                    }
                }
            }

            return covered.Count == expected.Count
                ? null
                : "Judge PASS does not list every acceptance criterion from the brief";
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
