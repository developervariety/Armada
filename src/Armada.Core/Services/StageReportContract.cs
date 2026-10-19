namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.RegularExpressions;

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

        #endregion

        #region Public-Methods

        /// <summary>
        /// The sections a persona's report must hold, in template order.
        /// </summary>
        /// <param name="persona">Persona name; the legacy Test Engineer name is accepted.</param>
        /// <returns>The required section names, or an empty list when the persona has no fixed contract.</returns>
        public static IReadOnlyList<string> RequiredSections(string? persona)
        {
            string normalized = PersonaCatalog.NormalizeName(persona);
            return _Sections.TryGetValue(normalized, out string[]? sections) ? sections : Array.Empty<string>();
        }

        /// <summary>
        /// The required sections a report lacks. A section is present when a Markdown heading of any level
        /// starts with its name, in any case.
        /// </summary>
        /// <param name="persona">Persona name.</param>
        /// <param name="report">The stage's report text.</param>
        /// <returns>The missing section names in template order; empty when none is missing.</returns>
        public static List<string> MissingSections(string? persona, string? report)
        {
            IReadOnlyList<string> required = RequiredSections(persona);
            if (required.Count == 0) return new List<string>();
            List<string> headings = String.IsNullOrEmpty(report)
                ? new List<string>()
                : _Heading.Matches(report.Replace("\r\n", "\n")).Select(match => match.Groups[1].Value.Trim().Trim('*', '`').Trim()).ToList();
            return required
                .Where(section => !headings.Any(heading => heading.StartsWith(section, StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }

        #endregion
    }
}
