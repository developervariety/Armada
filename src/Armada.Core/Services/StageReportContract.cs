namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.RegularExpressions;
    using Armada.Core.Enums;

    /// <summary>
    /// The report sections a persona's built-in template requires before its result line. A finished,
    /// successful stage whose report lacks one of them delivered a progress note or a partial answer, not
    /// the stage report, so its output artifact is not complete. Personas without a fixed section list
    /// (Worker, Architect, and Judge, which has its own verdict validation) have no contract here.
    /// </summary>
    public static class StageReportContract
    {
        #region Private-Members

        private static readonly Dictionary<string, string[]> _Sections = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            [PersonaCatalog.TestEngineer] = new[] { "Coverage Added", "Negative Paths", "Residual Risks" },
            [PersonaCatalog.ProductManager] = new[] { "Product Vision", "Use Cases", "Experience Requirements", "Validation", "Future Readiness" }
        };

        private static readonly Regex _Heading = new Regex(@"^\s{0,3}#{1,6}\s+(.+?)\s*#*\s*$", RegexOptions.Multiline | RegexOptions.Compiled);
        private static readonly Regex _JoinedProgressHeading = new Regex(
            @"^ {0,3}\[ARMADA:STATUS\] (?:InProgress|Testing|Review)#{1,6}[ \t]+(.+?)[ \t]*#*[ \t]*$", RegexOptions.Compiled);
        private static readonly Regex _Fence = new Regex(@"^ {0,3}(`{3,}|~{3,})(.*)$", RegexOptions.Compiled);

        #endregion

        #region Public-Methods

        /// <summary>
        /// The sections a persona's report must hold, in template order.
        /// </summary>
        /// <param name="persona">Persona name; the legacy Test Engineer name is accepted.</param>
        /// <param name="mode">Report-only modes use their generated report contract.</param>
        /// <returns>The required section names, or an empty list when the persona has no fixed contract.</returns>
        public static IReadOnlyList<string> RequiredSections(string? persona, MissionModeEnum mode = MissionModeEnum.Implementation)
        {
            string normalized = PersonaCatalog.NormalizeName(persona);
            // The generated Audit/Research prompts replace these producing personas' fixed
            // Implementation headings. Report content is still judged against the mission.
            if ((mode == MissionModeEnum.Audit || mode == MissionModeEnum.Research)
                && (normalized == PersonaCatalog.TestEngineer || normalized == PersonaCatalog.ProductManager))
                return Array.Empty<string>();
            return _Sections.TryGetValue(normalized, out string[]? sections) ? sections : Array.Empty<string>();
        }

        /// <summary>
        /// The required sections a report lacks. A section is present when a Markdown heading of any level
        /// starts with its name, in any case.
        /// </summary>
        /// <param name="persona">Persona name.</param>
        /// <param name="report">The stage's report text.</param>
        /// <param name="mode">Mission mode that selects the generated output contract.</param>
        /// <returns>The missing section names in template order; empty when none is missing.</returns>
        public static List<string> MissingSections(string? persona, string? report, MissionModeEnum mode = MissionModeEnum.Implementation)
        {
            IReadOnlyList<string> required = RequiredSections(persona, mode);
            if (required.Count == 0) return new List<string>();
            List<string> headings = String.IsNullOrEmpty(report)
                ? new List<string>()
                : _Heading.Matches(report.Replace("\r\n", "\n")).Select(match => match.Groups[1].Value.Trim().Trim('*', '`').Trim()).ToList();
            if (!String.IsNullOrEmpty(report)) headings.AddRange(ReadJoinedProgressHeadings(report));
            return required
                .Where(section => !headings.Any(heading => heading.StartsWith(section, StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }

        private static List<string> ReadJoinedProgressHeadings(string report)
        {
            // Historical captures can join a progress line directly to the next Markdown heading.
            // Recognize that heading only; neither rewrite the artifact nor treat the status as authority.
            List<string> headings = new List<string>();
            char fenceCharacter = '\0';
            int fenceLength = 0;
            foreach (string line in report.Replace("\r\n", "\n").Split('\n'))
            {
                Match fence = _Fence.Match(line);
                if (fence.Success)
                {
                    string delimiter = fence.Groups[1].Value;
                    if (fenceCharacter == '\0')
                    {
                        fenceCharacter = delimiter[0];
                        fenceLength = delimiter.Length;
                    }
                    else if (delimiter[0] == fenceCharacter && delimiter.Length >= fenceLength
                        && String.IsNullOrWhiteSpace(fence.Groups[2].Value))
                    {
                        fenceCharacter = '\0';
                        fenceLength = 0;
                    }
                    continue;
                }
                if (fenceCharacter != '\0') continue;
                Match heading = _JoinedProgressHeading.Match(line);
                if (heading.Success) headings.Add(heading.Groups[1].Value.Trim().Trim('*', '`').Trim());
            }
            return headings;
        }

        #endregion
    }
}
