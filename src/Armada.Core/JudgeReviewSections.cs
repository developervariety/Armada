namespace Armada.Core
{
    using System;
    using System.Collections.Generic;
    using System.Text.RegularExpressions;
    using Armada.Core.Enums;
    using Armada.Core.Services;

    /// <summary>
    /// The review sections a Judge must emit, by mission mode. Every brief, prompt and output
    /// contract that names them reads this one source, and so does the verdict validator, so a Judge
    /// can never be told to write one set while its PASS is judged against another. When the brief
    /// lists acceptance criteria the Judge also writes the <c>## Acceptance Criteria</c> walk
    /// (<see cref="JudgeAcceptanceWalk"/>); <see cref="Missing"/> reports both, for the verdict check
    /// and for the mission output artifact alike.
    /// </summary>
    public static class JudgeReviewSections
    {
        /// <summary>
        /// Verdict section name, required in every mode and validated through the verdict line.
        /// </summary>
        public const string Verdict = "Verdict";

        private static readonly string[] _Implementation = new string[] { "Completeness", "Correctness", "Tests", "Failure Modes" };
        private static readonly string[] _ReportOnly = new string[] { "Completeness", "Correctness", "Evidence", "Residual Risks" };

        /// <summary>
        /// Whether a mission mode delivers a report instead of a code change.
        /// </summary>
        /// <param name="mode">Mission mode.</param>
        /// <returns>True for Audit and Research.</returns>
        public static bool IsReportOnly(MissionModeEnum mode)
        {
            return mode == MissionModeEnum.Audit || mode == MissionModeEnum.Research;
        }

        /// <summary>
        /// The sections a Judge PASS must contain. A report-only mission is judged on its evidence
        /// and residual risks; an implementation mission on its tests and failure modes.
        /// </summary>
        /// <param name="reportOnly">True for a read-only (Audit or Research) mission.</param>
        /// <returns>The required section names, without the verdict section.</returns>
        public static IReadOnlyList<string> Required(bool reportOnly)
        {
            return reportOnly ? _ReportOnly : _Implementation;
        }

        /// <summary>
        /// The sections a Judge PASS must contain, for a mission mode.
        /// </summary>
        /// <param name="mode">Mission mode.</param>
        /// <returns>The required section names, without the verdict section.</returns>
        public static IReadOnlyList<string> Required(MissionModeEnum mode)
        {
            return Required(IsReportOnly(mode));
        }

        /// <summary>
        /// The required sections plus the verdict section, rendered as the heading list a captain is
        /// told to emit, for example "`## Completeness`, `## Correctness`, `## Evidence`,
        /// `## Residual Risks`, and `## Verdict`".
        /// </summary>
        /// <param name="reportOnly">True for a read-only (Audit or Research) mission.</param>
        /// <returns>The rendered heading list.</returns>
        public static string Headings(bool reportOnly)
        {
            IReadOnlyList<string> required = Required(reportOnly);
            List<string> headings = new List<string>();
            foreach (string section in required) headings.Add("`## " + section + "`");
            headings.Add("`## " + Verdict + "`");

            string result = String.Empty;
            for (int i = 0; i < headings.Count; i++)
            {
                if (i > 0) result += i == headings.Count - 1 ? ", and " : ", ";
                result += headings[i];
            }
            return result;
        }

        /// <summary>
        /// Whether a Judge report holds a section. A Judge writes its headings by hand, so they arrive
        /// wrapped in whatever markdown the model chose (<c>## Tests</c>, <c>**## Tests**</c>,
        /// <c>- __Tests__:</c>); the decoration is stripped and the heading TEXT is matched, because a
        /// review is rejected for what it fails to say, never for how it was formatted.
        /// </summary>
        /// <param name="report">The Judge report.</param>
        /// <param name="section">The section name.</param>
        /// <returns>True when a line names the section.</returns>
        public static bool Contains(string? report, string section)
        {
            if (String.IsNullOrWhiteSpace(report) || String.IsNullOrWhiteSpace(section)) return false;

            string pattern = @"^(?:\d+[\.\)]\s*)?" + Regex.Escape(section) + @"(?:$|[\s:\-\u2013\u2014(].*$)";
            foreach (string rawLine in report.Replace("\r\n", "\n").Split('\n'))
            {
                string normalized = rawLine.Trim().Trim('*', '_', '`', '#', '>', '-', ' ', ':').Trim();
                if (String.IsNullOrEmpty(normalized)) continue;
                if (Regex.IsMatch(normalized, pattern, RegexOptions.IgnoreCase)) return true;
            }

            return false;
        }

        /// <summary>
        /// The sections a Judge report lacks: each required review section for the mode, then the
        /// acceptance-criteria walk when the brief lists criteria. The verdict check refuses a PASS for
        /// exactly these, and the mission output artifact reports exactly these.
        /// </summary>
        /// <param name="report">The Judge report.</param>
        /// <param name="reportOnly">True for a read-only (Audit or Research) mission.</param>
        /// <param name="brief">The mission description the Judge was given; null skips the walk.</param>
        /// <returns>The missing section names in brief order; empty when none is missing.</returns>
        public static List<string> Missing(string? report, bool reportOnly, string? brief)
        {
            List<string> missing = new List<string>();
            foreach (string section in Required(reportOnly))
            {
                if (!Contains(report, section)) missing.Add(section);
            }

            if (brief != null && JudgeAcceptanceWalk.LacksWalkSection(report, brief))
                missing.Add(JudgeAcceptanceWalk.SectionName);
            return missing;
        }

        /// <summary>
        /// The required sections plus the verdict section, rendered for a mission mode.
        /// </summary>
        /// <param name="mode">Mission mode.</param>
        /// <returns>The rendered heading list.</returns>
        public static string Headings(MissionModeEnum mode)
        {
            return Headings(IsReportOnly(mode));
        }
    }
}
