namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Text;
    using System.Text.RegularExpressions;
    using Armada.Core.Models;
    using Armada.Core.Settings;

    public partial class MissionService
    {
        #region Private-Members

        /// <summary>
        /// Instruction filenames that only Armada writes. No runtime reads them by itself and no repository
        /// needs one, so a brief that names one the dock does not hold names a file from another runtime's dock.
        /// CLAUDE.md, AGENTS.md and GEMINI.md are left alone: repositories track them as their own content.
        /// </summary>
        private static readonly string[] _ArmadaOnlyInstructionFileNames = new[] { "CODEX.md", "CURSOR.md", "MUX.md" };

        private static readonly Regex _BriefingPathPattern = new Regex(
            @"(?<![\w./-])_briefing/[\w./-]*[\w-]",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex _NoTestEditsNonGoalPattern = new Regex(
            @"\b(?:no|without|never|not|don't)\s+(?:new\s+|added\s+|additional\s+)?" +
            @"(?:(?:add|adding|write|writing|edit|editing|modify|modifying|change|changing|create|creating|commit|committing|touch|touching)" +
            @"(?:\s*,\s*(?:or\s+)?(?:add|edit|modify|change|create|commit|write|touch))*\s+(?:or\s+\w+\s+)?(?:any\s+|new\s+|the\s+)?)?" +
            @"(?:unit\s+|integration\s+)?tests?\b" +
            @"|\btests?(?:\s+files?|\s+suites?)?\s+(?:are\s+|stay\s+|remain\s+)?(?:unchanged|untouched|out\s+of\s+scope|read-only)" +
            @"|\b(?:no|not|never|without)\b[^.;\n]{0,30}\b(?:production|source|code)\s+(?:or|and|nor)\s+tests?\b",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        #endregion

        #region Private-Methods

        /// <summary>
        /// Replace each Armada-only instruction filename the text names with the filename this runtime's dock
        /// holds. A handoff written on one runtime (a Codex Architect's reads list) otherwise sends a captain on
        /// another runtime to look for a file its dock never receives. A filename present at the checkout root
        /// is repository content and is kept.
        /// </summary>
        /// <param name="text">Mission description, or null.</param>
        /// <param name="instructionsFileName">The instruction filename this captain's runtime receives.</param>
        /// <param name="worktreePath">Dock worktree path.</param>
        /// <returns>The text with foreign instruction filenames replaced.</returns>
        internal static string? RetargetInstructionFileNames(string? text, string instructionsFileName, string worktreePath)
        {
            if (String.IsNullOrEmpty(text) || String.IsNullOrEmpty(instructionsFileName)) return text;

            string result = text;
            foreach (string foreign in _ArmadaOnlyInstructionFileNames)
            {
                if (String.Equals(foreign, instructionsFileName, StringComparison.Ordinal)) continue;
                if (result.IndexOf(foreign, StringComparison.Ordinal) < 0) continue;
                if (!String.IsNullOrEmpty(worktreePath) && File.Exists(Path.Combine(worktreePath, foreign))) continue;

                result = Regex.Replace(
                    result,
                    @"(?<![\w./-])" + Regex.Escape(foreign) + @"(?![\w-])",
                    instructionsFileName,
                    RegexOptions.CultureInvariant);
            }

            return result;
        }

        /// <summary>
        /// The <c>_briefing/</c> paths the text names that are neither a file nor a folder in the dock, in the
        /// order they first appear. Staged inputs are written before the brief is generated, so a path absent
        /// here is absent when the captain starts.
        /// </summary>
        /// <param name="text">Mission description, or null.</param>
        /// <param name="worktreePath">Dock worktree path.</param>
        /// <returns>The absent paths; empty when every named path is staged.</returns>
        internal static List<string> FindUnstagedBriefingPaths(string? text, string worktreePath)
        {
            List<string> absent = new List<string>();
            if (String.IsNullOrEmpty(text) || String.IsNullOrEmpty(worktreePath)) return absent;

            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match match in _BriefingPathPattern.Matches(text))
            {
                string path = match.Value.TrimEnd('.');
                if (!seen.Add(path)) continue;

                string full = Path.Combine(worktreePath, path.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(full) || Directory.Exists(full)) continue;
                absent.Add(path);
            }

            return absent;
        }

        /// <summary>
        /// The section that lists the staged paths the brief names but the dock does not hold, so a captain does
        /// not search for them or report each one. Empty when every named path is staged.
        /// </summary>
        /// <param name="absent">Absent paths from <see cref="FindUnstagedBriefingPaths(string?, string)"/>.</param>
        /// <returns>The section, or an empty string.</returns>
        internal static string BuildUnstagedBriefingPathsSection(List<string> absent)
        {
            if (absent == null || absent.Count == 0) return String.Empty;

            StringBuilder sb = new StringBuilder();
            sb.Append("## Named Files Not Staged\n");
            sb.Append("The brief names these paths, but they are not in this dock. Do not search for them; use the brief text instead:\n");
            foreach (string path in absent) sb.Append("- `").Append(path).Append("`\n");
            return sb.ToString();
        }

        /// <summary>
        /// Whether a non-goal forbids test edits (for example "No new tests" or "Do not add, edit, or commit test
        /// files"). A TestEngineer on such an objective validates only.
        /// </summary>
        /// <param name="nonGoals">The objective's non-goals.</param>
        /// <returns>True when any non-goal forbids test edits.</returns>
        internal static bool NonGoalsForbidTestEdits(IEnumerable<string>? nonGoals)
        {
            if (nonGoals == null) return false;
            foreach (string nonGoal in nonGoals)
            {
                if (!String.IsNullOrWhiteSpace(nonGoal) && _NoTestEditsNonGoalPattern.IsMatch(nonGoal)) return true;
            }
            return false;
        }

        /// <summary>
        /// The rule-precedence lines that close the Objective Scope non-goals: a non-goal wins over any role,
        /// rescue or captain duty that conflicts with it, and a TestEngineer on an objective that forbids test
        /// edits validates only.
        /// </summary>
        /// <param name="persona">Persona of the mission the brief is for.</param>
        /// <param name="nonGoals">The objective's non-goals.</param>
        /// <returns>The lines, or an empty string when the objective has no non-goals.</returns>
        internal static string BuildNonGoalPrecedenceLines(string? persona, List<string>? nonGoals)
        {
            if (nonGoals == null || nonGoals.Count == 0) return String.Empty;

            string lines = "When a role, rescue or captain duty conflicts with a non-goal, the non-goal wins; follow it and report the conflict in one line.\n";
            if (PersonaCatalog.Matches(persona, PersonaCatalog.TestEngineer) && NonGoalsForbidTestEdits(nonGoals))
            {
                lines += "These non-goals forbid test edits, so this TestEngineer stage is validate-only: run the validation the objective permits, report coverage gaps as findings, and commit no test files.\n";
            }
            return lines + "\n";
        }

        /// <summary>
        /// Whether the prior-art captain tool will answer unavailable for every call on this vessel: the vessel's
        /// content may not leave the host, the captain tools are disabled, or the prior_art decision is Off
        /// (which includes the global kill switch).
        /// </summary>
        /// <param name="settings">Typed-decision settings.</param>
        /// <param name="vessel">Vessel the mission runs on.</param>
        /// <returns>True when the tool is off for this vessel.</returns>
        internal static bool PriorArtToolOffFor(TypedDecisionSettings settings, Vessel vessel)
        {
            if (settings == null || vessel == null) return false;
            if (!settings.AllowsEgress(vessel.Id)) return true;
            if (!settings.CaptainTool.Enabled) return true;
            return settings.For("prior_art").Mode == Armada.Core.Enums.TypedDecisionModeEnum.Off;
        }

        #endregion
    }
}
