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
            if (String.IsNullOrWhiteSpace(agentOutput)) return reference;

            string source = agentOutput.Replace("\r\n", "\n");
            List<string> parts = new List<string>();

            string verdict = VerdictOf(source);
            if (verdict.Length > 0) parts.Add("Verdict:\n" + verdict);

            string blocking = BlockingFindings(source, Math.Max(400, maxChars / 2));
            if (blocking.Length > 0) parts.Add("Blocking findings (every one; fix all of them):\n" + blocking);

            foreach (string section in KeptSections(source))
                parts.Add(section);

            StringBuilder body = new StringBuilder(reference);
            foreach (string part in parts)
            {
                int room = maxChars - body.Length - 2;
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
        /// Every item of a review that is marked as blocking, one per line, each with its continuation
        /// lines. Tool narration before the first section header is skipped. When the items exceed the
        /// bound, each keeps its opening and says how much was cut, so no blocking item is dropped.
        /// Empty when the review marks nothing as blocking.
        /// </summary>
        /// <param name="review">Review or report text.</param>
        /// <param name="maxChars">Bound for the list.</param>
        /// <returns>One finding per line, each starting with "- ".</returns>
        public static string BlockingFindings(string? review, int maxChars)
        {
            if (String.IsNullOrWhiteSpace(review)) return String.Empty;
            string text = review.Replace("\r\n", "\n");
            int firstSection = IndexOfFirstSectionHeader(text);
            string body = firstSection > 0 ? text.Substring(firstSection) : text;
            string[] lines = body.Split('\n');

            List<string> findings = new List<string>();
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
                // "Non-blocking" contains a blocking marker; strip it before matching.
                string probe = Regex.Replace(line, "non-blocking", String.Empty, RegexOptions.IgnoreCase);
                if (!_BlockingMarkers.Any(marker => probe.Contains(marker, StringComparison.Ordinal))) continue;

                StringBuilder item = new StringBuilder(line);
                while (i + 1 < lines.Length && IsContinuation(lines[i + 1]))
                {
                    i++;
                    item.Append(' ').Append(lines[i].Trim());
                }
                findings.Add(item.ToString());
            }
            if (findings.Count == 0) return String.Empty;

            int total = findings.Sum(item => item.Length + 3);
            if (total <= maxChars)
                return String.Join("\n", findings.Select(item => "- " + item));

            int perItem = Math.Max(160, maxChars / findings.Count - 60);
            return String.Join("\n", findings.Select(item => item.Length <= perItem
                ? "- " + item
                : "- " + item.Substring(0, perItem).TrimEnd() + " ... (" + (item.Length - perItem) + " more chars of this finding in the review)"));
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
