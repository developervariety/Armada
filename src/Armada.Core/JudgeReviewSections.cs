namespace Armada.Core
{
    using System;
    using System.Collections.Generic;
    using Armada.Core.Enums;

    /// <summary>
    /// The review sections a Judge must emit, by mission mode. Every brief, prompt and output
    /// contract that names them reads this one source, and so does the verdict validator, so a Judge
    /// can never be told to write one set while its PASS is judged against another.
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
