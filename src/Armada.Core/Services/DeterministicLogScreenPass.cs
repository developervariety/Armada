namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Text.RegularExpressions;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Settings;

    /// <summary>
    /// The deterministic screening pass: it matches mechanical shapes in a captain log tail with no
    /// model call and no provider key, so it is the fallback that keeps the screen useful when no
    /// model is configured. Every class it reports is a shape a rule can settle by itself; a shape
    /// that needs judgement belongs in another pass.
    ///
    /// The pass is conservative by construction. A finding costs an operator a glance at the board,
    /// so a rule that cannot separate the violation from the thing that merely looks like it reports
    /// nothing.
    /// </summary>
    public class DeterministicLogScreenPass : ICaptainLogScreenPass
    {
        #region Public-Members

        /// <inheritdoc />
        public string Name => "deterministic";

        /// <summary>
        /// A success claim whose only evidence is a tool's own success message.
        /// </summary>
        public const string RuleUnprovedFix = "unproved_fix";

        /// <summary>
        /// A build piped into a text search and chained into a test run, so the suite runs whenever
        /// the search matched, including on an error line.
        /// </summary>
        public const string RulePipeGatedBuild = "pipe_gated_build";

        /// <summary>
        /// Added content that declines to act without saying why: an empty catch, or a skip with no
        /// stated reason.
        /// </summary>
        public const string RuleSilentSkip = "silent_skip";

        /// <summary>
        /// A plan-block label in added content. A label is a dispatch artifact and is meaningless
        /// once it is separated from the plan, so it never belongs in committed content.
        /// </summary>
        public const string RulePlanLabel = "plan_label";

        /// <summary>
        /// A line holding one of the operator-configured boundary patterns.
        /// </summary>
        public const string RuleBoundaryToken = "boundary_token";

        /// <summary>The rule classes this pass can report, in catalogue order.</summary>
        public static IReadOnlyList<string> RuleClasses { get; } = new List<string>
        {
            RuleUnprovedFix,
            RulePipeGatedBuild,
            RuleSilentSkip,
            RulePlanLabel,
            RuleBoundaryToken
        };

        #endregion

        #region Private-Members

        private const int _MaxFindingsPerClass = 20;
        private const int _MaxEvidenceLength = 240;

        // A success claim and a tool's own success message on one line. Neither half alone is a
        // finding: a build that reports success is ordinary output, and a claim that names a re-run
        // is the proof the rule asks for.
        private static readonly string[] _ProofClaimPhrases =
        {
            "fix verified", "fix is verified", "verified the fix", "fix confirmed", "confirmed the fix",
            "fix is confirmed", "proves the fix", "fix proven", "fix is proven", "fix is proved",
            "confirms the fix", "this proves the fix"
        };

        private static readonly string[] _ToolSuccessPhrases =
        {
            "build succeeded", "all tests pass", "all tests passed", "tests passed", "0 errors",
            "exit code 0", "exit 0", "reports success", "success message", "reported success"
        };

        // A re-run of the failing command anywhere in the tail is the evidence the rule wants, so it
        // suppresses the class for that tail rather than weighing against one line.
        private static readonly string[] _ReRunPhrases =
        {
            "re-ran", "reran", "re-run of", "ran it again", "reproduced the original",
            "reproduced the failure", "re-executed", "before and after"
        };

        // A build piped into a text search, then chained into a test run. The chain is what makes it
        // a gate: the suite runs whenever the search MATCHED, including on an error line.
        private static readonly Regex _PipeGatedBuildPattern = new Regex(
            @"\bbuild\b[^|\r\n]*\|[^&\r\n]*\bgrep\b[^\r\n]*&&[^\r\n]*\b(test|tests|suite)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        // A plan-block label: a capital M, digits, then a colon.
        private static readonly Regex _PlanLabelPattern = new Regex(
            @"\bM\d+:",
            RegexOptions.CultureInvariant | RegexOptions.Compiled);

        // A catch clause, with or without an exception declaration, opening its block on the line.
        private static readonly Regex _CatchOpenPattern = new Regex(
            @"\bcatch\b\s*(\([^)]*\))?\s*(\{|$)",
            RegexOptions.CultureInvariant | RegexOptions.Compiled);

        // A single-line catch whose block is empty.
        private static readonly Regex _EmptyCatchInlinePattern = new Regex(
            @"\bcatch\b\s*(\([^)]*\))?\s*\{\s*\}",
            RegexOptions.CultureInvariant | RegexOptions.Compiled);

        // A skip whose reason is absent or an empty string.
        private static readonly Regex _ReasonlessSkipPattern = new Regex(
            @"\b(Skip|Ignore|Skipped)\s*(=\s*(""""|"" "")|\(\s*(""""|"" "")?\s*\))",
            RegexOptions.CultureInvariant | RegexOptions.Compiled);

        private readonly CaptainLogScreeningSettings _Settings;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="settings">Screening settings. The boundary-pattern list is read from this
        /// instance on every evaluation, so an operator edit reaches the pass without a restart.</param>
        public DeterministicLogScreenPass(CaptainLogScreeningSettings settings)
        {
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        #endregion

        #region Public-Methods

        /// <inheritdoc />
        public Task<IReadOnlyList<LogScreenFinding>> EvaluateAsync(LogScreenContext context, CancellationToken token)
        {
            List<LogScreenFinding> findings = new List<LogScreenFinding>();
            if (context == null || String.IsNullOrEmpty(context.Tail))
                return Task.FromResult<IReadOnlyList<LogScreenFinding>>(findings);

            string[] lines = context.Tail.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            bool[] inFence = MarkFencedLines(lines);
            bool[] isAdded = MarkAddedLines(lines);
            bool tailNamesReRun = ContainsAny(context.Tail, _ReRunPhrases);

            AddUnprovedFix(lines, tailNamesReRun, findings);
            AddPipeGatedBuild(lines, findings);
            AddSilentSkip(lines, isAdded, findings);
            AddPlanLabel(lines, isAdded, findings);
            AddBoundaryToken(lines, inFence, findings);

            return Task.FromResult<IReadOnlyList<LogScreenFinding>>(findings);
        }

        #endregion

        #region Private-Methods

        private static void AddUnprovedFix(string[] lines, bool tailNamesReRun, List<LogScreenFinding> findings)
        {
            // A re-run of the failing command anywhere in the tail is exactly the evidence this class
            // asks for, so the class is not reported at all for such a tail.
            if (tailNamesReRun) return;

            int found = 0;
            foreach (string line in lines)
            {
                if (found >= _MaxFindingsPerClass) return;
                if (!ContainsAny(line, _ProofClaimPhrases)) continue;
                if (!ContainsAny(line, _ToolSuccessPhrases)) continue;
                findings.Add(Build(RuleUnprovedFix, line,
                    "A success claim whose evidence is a tool's own success message, with no re-run of the failing command in the tail."));
                found++;
            }
        }

        private static void AddPipeGatedBuild(string[] lines, List<LogScreenFinding> findings)
        {
            int found = 0;
            foreach (string line in lines)
            {
                if (found >= _MaxFindingsPerClass) return;
                if (!_PipeGatedBuildPattern.IsMatch(line)) continue;
                findings.Add(Build(RulePipeGatedBuild, line,
                    "A build is piped into a text search that then chains a test run, so the suite runs whenever the search matched."));
                found++;
            }
        }

        private static void AddSilentSkip(string[] lines, bool[] isAdded, List<LogScreenFinding> findings)
        {
            int found = 0;
            for (int i = 0; i < lines.Length; i++)
            {
                if (found >= _MaxFindingsPerClass) return;
                if (!isAdded[i]) continue;

                string body = StripDiffMarker(lines[i]);

                if (_ReasonlessSkipPattern.IsMatch(body))
                {
                    findings.Add(Build(RuleSilentSkip, lines[i],
                        "Added content skips with no stated reason, so the skip reads as a healthy idle path."));
                    found++;
                    continue;
                }

                if (_EmptyCatchInlinePattern.IsMatch(body))
                {
                    findings.Add(Build(RuleSilentSkip, lines[i],
                        "Added content holds an empty catch. A condition worth catching is worth counting and naming."));
                    found++;
                    continue;
                }

                if (!_CatchOpenPattern.IsMatch(body)) continue;
                if (IsEmptyCatchBlock(lines, isAdded, i))
                {
                    findings.Add(Build(RuleSilentSkip, lines[i],
                        "Added content holds an empty catch. A condition worth catching is worth counting and naming."));
                    found++;
                }
            }
        }

        /// <summary>
        /// Walks the added lines after a catch that opens a block and reports whether the block closes
        /// with no statement in it. A brace on its own line and a brace on the catch line are both
        /// handled; a body line, a comment, or a break in the added run all end the walk.
        /// </summary>
        private static bool IsEmptyCatchBlock(string[] lines, bool[] isAdded, int catchIndex)
        {
            bool opened = StripDiffMarker(lines[catchIndex]).Contains('{');
            for (int i = catchIndex + 1; i < lines.Length; i++)
            {
                if (!isAdded[i]) return false;
                string body = StripDiffMarker(lines[i]).Trim();
                if (body.Length == 0) continue;
                if (!opened)
                {
                    if (body == "{") { opened = true; continue; }
                    return false;
                }
                return body == "}";
            }
            return false;
        }

        private static void AddPlanLabel(string[] lines, bool[] isAdded, List<LogScreenFinding> findings)
        {
            int found = 0;
            for (int i = 0; i < lines.Length; i++)
            {
                if (found >= _MaxFindingsPerClass) return;
                if (!isAdded[i]) continue;
                if (!_PlanLabelPattern.IsMatch(StripDiffMarker(lines[i]))) continue;
                findings.Add(Build(RulePlanLabel, lines[i],
                    "Added content carries a plan-block label, which is meaningless once it is separated from the plan."));
                found++;
            }
        }

        private void AddBoundaryToken(string[] lines, bool[] inFence, List<LogScreenFinding> findings)
        {
            List<string> patterns = _Settings.BoundaryPatterns;
            if (patterns == null || patterns.Count == 0) return;

            int found = 0;
            for (int i = 0; i < lines.Length; i++)
            {
                if (found >= _MaxFindingsPerClass) return;
                // A pattern inside a fenced quote is quoted material, not an occurrence in the work.
                if (inFence[i]) continue;
                foreach (string pattern in patterns)
                {
                    if (String.IsNullOrWhiteSpace(pattern)) continue;
                    if (lines[i].IndexOf(pattern, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    findings.Add(Build(RuleBoundaryToken, lines[i],
                        "The line holds an operator-configured boundary pattern."));
                    found++;
                    break;
                }
            }
        }

        /// <summary>
        /// Marks the lines inside a fenced block. The fence line itself is marked, so a pattern on the
        /// opening fence is treated as quoted too.
        /// </summary>
        private static bool[] MarkFencedLines(string[] lines)
        {
            bool[] result = new bool[lines.Length];
            bool inside = false;
            for (int i = 0; i < lines.Length; i++)
            {
                bool isFence = lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal);
                if (isFence)
                {
                    result[i] = true;
                    inside = !inside;
                    continue;
                }
                result[i] = inside;
            }
            return result;
        }

        /// <summary>
        /// Marks the lines that are added content of a quoted diff. A line counts as added only inside
        /// a hunk, so a prose line that happens to open with a plus sign is not read as committed work,
        /// and neither is the "+++" file header.
        /// </summary>
        private static bool[] MarkAddedLines(string[] lines)
        {
            bool[] result = new bool[lines.Length];
            bool inHunk = false;
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line.StartsWith("@@", StringComparison.Ordinal)) { inHunk = true; continue; }
                if (line.StartsWith("diff --git", StringComparison.Ordinal)) { inHunk = false; continue; }
                if (line.StartsWith("+++", StringComparison.Ordinal) || line.StartsWith("---", StringComparison.Ordinal)) continue;
                if (!inHunk) continue;
                if (line.StartsWith("+", StringComparison.Ordinal)) { result[i] = true; continue; }
                if (line.Length > 0 && line[0] != ' ' && line[0] != '-' && line[0] != '\\') inHunk = false;
            }
            return result;
        }

        private static string StripDiffMarker(string line)
        {
            return line.StartsWith("+", StringComparison.Ordinal) ? line.Substring(1) : line;
        }

        private static bool ContainsAny(string haystack, string[] needles)
        {
            foreach (string needle in needles)
                if (haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        private static LogScreenFinding Build(string ruleClass, string line, string detail)
        {
            string evidence = line.Trim();
            if (evidence.Length > _MaxEvidenceLength) evidence = evidence.Substring(0, _MaxEvidenceLength);
            return new LogScreenFinding
            {
                RuleClass = ruleClass,
                EvidenceLine = evidence,
                Source = LogScreenFinding.SourceRule,
                Confidence = null,
                Detail = detail
            };
        }

        #endregion
    }
}
