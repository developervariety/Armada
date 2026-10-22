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
        #region Public-Members

        /// <summary>
        /// The rule that decides a read-only mission whose acceptance criteria require a build or test run.
        /// Read-only forbids changes to the repository, not verification, so the criterion wins over the
        /// read-only default against running builds and tests, and every other read-only rule still holds.
        /// </summary>
        public const string ReadOnlyVerificationRunRule =
            "An acceptance criterion requires running builds or tests, and that criterion wins over the read-only default against running them: " +
            "run the existing build and test commands the criterion needs, and quote each command with its totals. " +
            "This rule wins over any other line in this brief that forbids running builds or tests. " +
            "Read-only still holds: change no tracked file, and do not commit, stage, or push. " +
            "Run no other command that changes repository state. " +
            "State this conflict and the rule you followed in one line of your report.";

        #endregion

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

        private static readonly Regex _VerificationRunCriterionPattern = new Regex(
            @"\b(?:run|runs|running|re-run|rerun|execute|executes|executing)\b[^.;\n]{0,60}?\b(?:tests?|test\s+class(?:es)?|suites?|gates?|builds?|checks?)\b" +
            @"|\b(?:class|suite|test)\s+runs?\b" +
            @"|\b\d+\s*/\s*\d+\s+(?:pass|passed|green)\b" +
            @"|\b0\s+(?:failures?|failed|errors?)\b" +
            @"|\b(?:builds?|tests?|suites?|gates?)\b[^.;\n]{0,60}?\b(?:pass|passes|passed|(?:is|are|stays?)\s+green|succeeds?|succeeded)\b" +
            @"|\b(?:dotnet|npm|pnpm|yarn|cargo|go|mvn|gradle)\s+(?:test|build)\b|\bpytest\b|\bmake\s+(?:test|check)\b",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        private static readonly Regex _VerificationRunNegationPattern = new Regex(
            @"\b(?:do\s+not|don't|never|without)\s+(?:run|running|re-run|rerun|execute|executing|build|building)\b",
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
            foreach (string foreign in FindForeignInstructionFileNames(text, instructionsFileName, worktreePath))
            {
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
        public static bool NonGoalsForbidTestEdits(IEnumerable<string>? nonGoals)
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
        /// Whether an acceptance criterion can only be met by running a build or tests: it asks to run or execute
        /// tests, a suite, a gate or a build, names class or suite runs, pins a pass count ("42/42 pass", "0
        /// failures"), says a build or suite passes, or names a build or test command. A criterion that forbids
        /// running them ("do not run tests") does not count.
        /// </summary>
        /// <param name="criteria">The acceptance criteria.</param>
        /// <returns>True when any criterion requires a build or test run.</returns>
        public static bool CriteriaRequireVerificationRuns(IEnumerable<string>? criteria)
        {
            return FindVerificationRunCriteria(criteria).Count > 0;
        }

        /// <summary>
        /// The acceptance criteria that require a build or test run, in their stored order (see
        /// <see cref="CriteriaRequireVerificationRuns(IEnumerable{string}?)"/>).
        /// </summary>
        /// <param name="criteria">The acceptance criteria.</param>
        /// <returns>The matching criteria, trimmed; empty when none requires a run.</returns>
        public static List<string> FindVerificationRunCriteria(IEnumerable<string>? criteria)
        {
            List<string> found = new List<string>();
            if (criteria == null) return found;
            foreach (string criterion in criteria)
            {
                if (String.IsNullOrWhiteSpace(criterion)) continue;
                if (_VerificationRunNegationPattern.IsMatch(criterion)) continue;
                if (_VerificationRunCriterionPattern.IsMatch(criterion)) found.Add(criterion.Trim());
            }
            return found;
        }

        /// <summary>
        /// The duty conflicts a dispatch preflight finds between the stages that will run and the objective,
        /// each stating which rule wins. A read-only dispatch whose criteria require a build or test run is
        /// decided by <see cref="ReadOnlyVerificationRunRule"/>; a TestEngineer stage on an objective whose
        /// non-goals forbid test edits is decided by the non-goal, and that stage validates only. The mission
        /// brief carries the same rule, so the preflight records what the captain will be told.
        /// </summary>
        /// <param name="readOnly">True when the dispatch runs in a read-only mode.</param>
        /// <param name="stagePersonas">Persona names of the stages that will run.</param>
        /// <param name="acceptanceCriteria">The objective's acceptance criteria.</param>
        /// <param name="nonGoals">The objective's non-goals.</param>
        /// <returns>One line per conflict; empty when the stages and the objective agree.</returns>
        public static List<string> FindStageDutyConflicts(
            bool readOnly,
            IEnumerable<string>? stagePersonas,
            IEnumerable<string>? acceptanceCriteria,
            IEnumerable<string>? nonGoals)
        {
            List<string> conflicts = new List<string>();
            if (readOnly)
            {
                List<string> runCriteria = FindVerificationRunCriteria(acceptanceCriteria);
                if (runCriteria.Count > 0)
                {
                    conflicts.Add("Read-only mode forbids running builds and tests by default, but these acceptance criteria require them: "
                        + String.Join(" | ", runCriteria)
                        + ". Rule that wins: the criterion. The brief lets each stage run the existing build and test commands the criterion needs; "
                        + "read-only still forbids edits, commits and pushes.");
                }
            }

            bool hasTestEngineer = false;
            if (stagePersonas != null)
            {
                foreach (string persona in stagePersonas)
                {
                    if (PersonaCatalog.Matches(persona, PersonaCatalog.TestEngineer)) { hasTestEngineer = true; break; }
                }
            }
            if (hasTestEngineer && !readOnly && NonGoalsForbidTestEdits(nonGoals))
            {
                conflicts.Add("The TestEngineer stage writes and commits tests, but a non-goal forbids test edits. "
                    + "Rule that wins: the non-goal. The brief tells the TestEngineer it is validate-only.");
            }
            return conflicts;
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
