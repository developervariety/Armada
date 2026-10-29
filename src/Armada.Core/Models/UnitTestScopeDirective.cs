namespace Armada.Core.Models
{
    using System;
    using System.Text.RegularExpressions;
    using Armada.Core.Enums;

    /// <summary>
    /// Parses the <c>[ARMADA:UNIT-TESTS] full</c> / <c>[ARMADA:UNIT-TESTS] abbreviated</c> directive that chooses which
    /// workflow profile unit-test command a mission's stage check runs, and resolves that command. A directive in the
    /// mission description (the operator choosing directly) wins over one in the stage persona's prompt (for example the
    /// Test Engineer prompt template); with neither, the full command runs.
    /// </summary>
    public static class UnitTestScopeDirective
    {
        /// <summary>
        /// The directive marker, followed on the same line by <c>full</c> or <c>abbreviated</c>.
        /// </summary>
        public const string Marker = "[ARMADA:UNIT-TESTS]";

        private static readonly Regex _Pattern = new Regex(
            @"\[ARMADA:UNIT-TESTS\][ \t]*(?<scope>full|abbreviated|quick|short)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        /// <summary>
        /// Find the unit-test scope a text asks for. The last directive in the text wins.
        /// </summary>
        /// <param name="text">Mission description or persona prompt; may be null.</param>
        /// <returns>The requested scope, or null when the text has no directive.</returns>
        public static UnitTestScopeEnum? Parse(string? text)
        {
            if (String.IsNullOrEmpty(text)) return null;
            MatchCollection matches = _Pattern.Matches(text);
            if (matches.Count == 0) return null;
            string scope = matches[matches.Count - 1].Groups["scope"].Value;
            return String.Equals(scope, "full", StringComparison.OrdinalIgnoreCase) ? UnitTestScopeEnum.Full : UnitTestScopeEnum.Abbreviated;
        }

        /// <summary>
        /// Resolve the scope for a mission: the mission description's directive first, then the persona prompt's, then Full.
        /// </summary>
        /// <param name="missionDescription">Mission description; may be null.</param>
        /// <param name="personaPrompt">Rendered persona prompt; may be null.</param>
        /// <returns>The scope to use.</returns>
        public static UnitTestScopeEnum Resolve(string? missionDescription, string? personaPrompt)
        {
            return Parse(missionDescription) ?? Parse(personaPrompt) ?? UnitTestScopeEnum.Full;
        }

        /// <summary>
        /// Pick the profile's unit-test command for a scope. Abbreviated falls back to the full command when unset, and
        /// Full falls back to the abbreviated one when only that is set.
        /// </summary>
        /// <param name="profile">Workflow profile.</param>
        /// <param name="scope">Requested scope.</param>
        /// <returns>The trimmed command, or null when the profile has neither.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="profile"/> is null.</exception>
        public static string? SelectCommand(WorkflowProfile profile, UnitTestScopeEnum scope)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            string? full = String.IsNullOrWhiteSpace(profile.UnitTestCommand) ? null : profile.UnitTestCommand!.Trim();
            string? abbreviated = String.IsNullOrWhiteSpace(profile.AbbreviatedUnitTestCommand) ? null : profile.AbbreviatedUnitTestCommand!.Trim();
            return scope == UnitTestScopeEnum.Abbreviated ? (abbreviated ?? full) : (full ?? abbreviated);
        }
    }
}
