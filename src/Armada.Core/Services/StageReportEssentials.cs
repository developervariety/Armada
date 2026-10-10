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
        /// Correctness, Failure Modes and Tests list items are considered defects; Verdict carries
        /// required changes. MET and explicitly non-blocking items are excluded. When the bound cannot
        /// hold every item, the result names the omission instead of claiming completeness.
        /// </summary>
        /// <param name="review">Review or report text.</param>
        /// <param name="maxChars">Bound for the list.</param>
        /// <returns>One finding per line, each starting with "- ".</returns>
        public static string BlockingFindings(string? review, int maxChars)
        {
            if (String.IsNullOrWhiteSpace(review)) return String.Empty;
            string text = review.Replace("\r\n", "\n");
            int firstSection = IndexOfFirstSectionHeader(text);
            if (firstSection > 0) text = text.Substring(firstSection);
            string[] lines = text.Split('\n');
            List<string> findings = new List<string>();
            List<string> criteria = new List<string>();
            string section = String.Empty;
            int protectedHeadingDepth = 0;
            for (int i = 0; i < lines.Length; i++)
            {
                string raw = lines[i].Trim();
                if (raw.StartsWith("#", StringComparison.Ordinal))
                {
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
                bool notMetCriterion = ContainsAny(raw, "NOT DELIVERED", "NOT MET", "NOT RESOLVED");
                if (IsExplicitlyNonBlocking(raw, item)) continue;
                bool acceptanceSection = section == "Completeness" || section == "Acceptance Criteria" || section == "Definition of Done";
                if (!notMetCriterion && acceptanceSection && IsCompletedCriterion(raw)) continue;
                if (!notMetCriterion && IsExplicitSuccess(item, section)) continue;

                bool explicitBlocking = IsExplicitBlocking(raw);
                bool sectionFinding = section == "Correctness" || section == "Failure Modes"
                    ? listItem
                    : section == "Tests"
                        ? listItem
                        : section == "Verdict"
                            ? ContainsAny(raw, "NEEDS_REVISION", "REQUIRED CHANGE", "MUST ") || (listItem && !ContainsAny(raw, "PASS"))
                            : false;
                if (!notMetCriterion && !explicitBlocking && !sectionFinding) continue;

                StringBuilder finding = new StringBuilder(item);
                while (i + 1 < lines.Length && IsContinuation(lines[i + 1]))
                {
                    i++;
                    finding.Append(' ').Append(lines[i].Trim());
                }
                string value = finding.ToString().Trim();
                List<string> destination = notMetCriterion ? criteria : findings;
                if (!destination.Contains(value, StringComparer.Ordinal)) destination.Add(value);
            }

            if (findings.Count == 0 && criteria.Count == 0) return String.Empty;
            List<string> formattedFindings = findings.Select(item => "- " + item).ToList();
            List<string> formattedCriteria = criteria.Select(item => "- " + item).ToList();
            List<string> formatted = new List<string>(formattedFindings);
            formatted.AddRange(formattedCriteria);
            int total = formatted.Sum(line => line.Length) + Math.Max(0, formatted.Count - 1);
            if (total <= maxChars) return String.Join("\n", formatted);

            string incomplete = IncompleteFindingsLine(maxChars);
            int defectTotal = formattedFindings.Sum(line => line.Length) + Math.Max(0, formattedFindings.Count - 1);
            if (defectTotal + incomplete.Length + 1 <= maxChars)
            {
                List<string> bounded = new List<string>(formattedFindings);
                int used = defectTotal;
                foreach (string criterion in formattedCriteria)
                {
                    int addedLength = criterion.Length + (bounded.Count > 0 ? 1 : 0);
                    if (used + addedLength + incomplete.Length + 1 > maxChars) break;
                    bounded.Add(criterion);
                    used += addedLength;
                }
                bounded.Add(incomplete);
                return String.Join("\n", bounded);
            }

            const string cutMarker = " ... [detail omitted]";
            int minimumItemLength = 2 + cutMarker.Length;
            int itemsToKeep = Math.Min(formattedFindings.Count, Math.Max(0, (maxChars - incomplete.Length - 1) / minimumItemLength));
            if (itemsToKeep == 0) return incomplete;
            int itemBudget = (maxChars - incomplete.Length - itemsToKeep) / itemsToKeep;
            List<string> shortened = new List<string>();
            for (int i = 0; i < itemsToKeep; i++)
            {
                string line = formattedFindings[i];
                if (line.Length <= itemBudget)
                {
                    shortened.Add(line);
                    continue;
                }
                int keep = itemBudget - 2 - cutMarker.Length;
                if (keep <= 0) return incomplete;
                shortened.Add(line.Substring(0, Math.Min(2 + keep, line.Length - cutMarker.Length)).TrimEnd() + cutMarker);
            }
            shortened.Add(incomplete);
            string boundedResult = String.Join("\n", shortened);
            return boundedResult.Length <= maxChars ? boundedResult : incomplete;
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
            if (name.StartsWith("Failure Modes", StringComparison.OrdinalIgnoreCase)) return "Failure Modes";
            if (name.StartsWith("Tests", StringComparison.OrdinalIgnoreCase)) return "Tests";
            if (name.StartsWith("Verdict", StringComparison.OrdinalIgnoreCase)) return "Verdict";
            if (name.StartsWith("Completeness", StringComparison.OrdinalIgnoreCase)) return "Completeness";
            if (name.StartsWith("Acceptance Criteria", StringComparison.OrdinalIgnoreCase)) return "Acceptance Criteria";
            if (name.StartsWith("Definition of Done", StringComparison.OrdinalIgnoreCase)) return "Definition of Done";
            return String.Empty;
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

        private static bool IsExplicitlyNonBlocking(string line, string item)
        {
            return HasLabelPrefix(line, "Non-blocking")
                || HasLabelPrefix(item, "Non-blocking")
                || HasLabelPrefix(line, "Non blocking")
                || HasLabelPrefix(item, "Non blocking")
                || HasLabelPrefix(line, "Lesser item")
                || HasLabelPrefix(item, "Lesser item")
                || HasLabelPrefix(line, "Lower priority")
                || HasLabelPrefix(item, "Lower priority");
        }

        private static bool IsCompletedCriterion(string line)
        {
            if (ContainsAny(line, "NOT MET", "NOT DELIVERED", "NOT RESOLVED")) return false;
            return Regex.IsMatch(line, @"\bMET\b", RegexOptions.IgnoreCase)
                || Regex.IsMatch(line, @"\bDELIVERED\b", RegexOptions.IgnoreCase);
        }

        private static bool IsExplicitSuccess(string item, string section)
        {
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
                if (line.Contains(marker, StringComparison.OrdinalIgnoreCase)) return true;
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

        private static string IncompleteFindingsLine(int maxChars)
        {
            string marker = "- " + IncompleteFindingsMarker + " Required items or details are omitted; read complete mission output with armada_mission_output before editing.";
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
