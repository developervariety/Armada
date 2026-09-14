namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text;
    using System.Text.RegularExpressions;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// Classifies the added lines of a unified .NET diff for reward-hacking patterns ("slop"):
    /// shortcuts that make a build or a suite go green without fixing the problem.
    /// </summary>
    /// <remarks>
    /// The split between FAIL and WARN is deliberate. A skipped test, a project-wide NoWarn and a
    /// central-package-version bypass have no faithful-reproduction reading, so they fail the
    /// check. An empty catch, a literal delay and a warning pragma can be exactly what a byte-exact
    /// port of a source file contains, so they are reported without failing.
    /// <para>
    /// Only ADDED lines are classified. Context and removed lines describe code the diff did not
    /// introduce. A finding is suppressed only by a marker naming its rule and recording a reason,
    /// on the flagged line or on the line directly above it:
    /// <c>slop-allow &lt;Rule&gt;: &lt;reason&gt;</c> inside a comment. A marker without a
    /// reason, or naming another rule, is not honored, and the finding says why.
    /// </para>
    /// <para>
    /// This class is the single definition of the rules and their severities. Every entry point
    /// that runs the Slop check calls it.
    /// </para>
    /// </remarks>
    public static class SlopDiffClassifier
    {
        #region Public-Members

        /// <summary>
        /// The word that opens a suppression marker.
        /// </summary>
        public const string SuppressionMarker = "slop-allow";

        /// <summary>
        /// The minimum length of a recorded suppression reason, after trimming.
        /// </summary>
        public const int MinimumReasonLength = 8;

        #endregion

        #region Private-Members

        private static readonly Regex _HunkHeader = new Regex(@"^@@ -\d+(?:,\d+)? \+(\d+)(?:,\d+)? @@", RegexOptions.Compiled);

        private static readonly Regex _SkipArgument = new Regex(
            @"\[[^\]]*\b(?:Fact|Theory|SkippableFact|SkippableTheory|Test|TestCase|TestMethod|DataTestMethod)(?:Attribute)?\s*\([^\]]*\bSkip\s*=",
            RegexOptions.Compiled);

        private static readonly Regex _IgnoreAttribute = new Regex(
            @"\[[^\]]*?\bIgnore(?:Attribute)?\s*(?:\([^\]]*\))?\s*[\],]",
            RegexOptions.Compiled);

        private static readonly Regex _DynamicSkip = new Regex(
            @"\b(?:Assert\.(?:Skip|Ignore|Inconclusive)|Skip\.(?:If|IfNot|Unless|When))\s*\(",
            RegexOptions.Compiled);

        private static readonly Regex _IfFalse = new Regex(@"^\s*#\s*if\s+false\b", RegexOptions.Compiled);

        private static readonly Regex _TestPathSegment = new Regex(@"(?:^|/)[^/]*tests?[^/]*(?:/|$)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex _WarningPragma = new Regex(@"^\s*#\s*pragma\s+warning\s+disable\b", RegexOptions.Compiled);

        private static readonly Regex _SuppressMessage = new Regex(
            @"\[\s*(?:assembly\s*:\s*|module\s*:\s*)?(?:[\w.]+\.)?SuppressMessage(?:Attribute)?\s*\(",
            RegexOptions.Compiled);

        private static readonly Regex _LiteralDelay = new Regex(
            @"\b(?:Task\.Delay|Thread\.Sleep)\s*\(\s*(?:TimeSpan\.From\w+\s*\(\s*)?\d",
            RegexOptions.Compiled);

        private static readonly Regex _NoWarnElement = new Regex(@"<\s*NoWarn\s*>", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex _InlinePackageVersion = new Regex(
            @"<\s*PackageReference\b[^>]*\bVersion\s*=",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex _VersionOverride = new Regex(
            @"\bVersionOverride\s*=|<\s*VersionOverride\s*>",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex _CentralManagementOptOut = new Regex(
            @"<\s*ManagePackageVersionsCentrally\s*>\s*false\s*<",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex _CentralManagementEnabled = new Regex(
            @"<\s*ManagePackageVersionsCentrally\s*>\s*true\s*<",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex _EmptyCatch = new Regex(
            @"\bcatch\b\s*(?:\([^)]*\))?\s*(?:when\s*\((?:[^()]|\((?:[^()]|\([^()]*\))*\))*\))?\s*\{\s*\}",
            RegexOptions.Compiled);

        private static readonly Regex _CatchKeyword = new Regex(@"\bcatch\b", RegexOptions.Compiled);

        private static readonly Regex _BlockComment = new Regex(@"/\*.*?\*/", RegexOptions.Compiled | RegexOptions.Singleline);

        private static readonly Regex _LineComment = new Regex(@"//[^\n]*", RegexOptions.Compiled);

        private static readonly Regex _Marker = new Regex(
            @"\bslop-allow\s+(\w+)\s*:?\s*(.*?)\s*(?:\*/|-->)?\s*$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private const int _MaxCatchWindowLines = 4;

        #endregion

        #region Public-Methods

        /// <summary>
        /// The severity a rule carries.
        /// </summary>
        /// <param name="rule">The rule.</param>
        /// <returns>Fail for unambiguous slop; Warn for reproduction-ambiguous patterns.</returns>
        public static SlopSeverityEnum SeverityOf(SlopRuleEnum rule)
        {
            switch (rule)
            {
                case SlopRuleEnum.SkippedTest:
                case SlopRuleEnum.ProjectWideNoWarn:
                case SlopRuleEnum.CentralPackageVersionBypass:
                    return SlopSeverityEnum.Fail;
                case SlopRuleEnum.EmptyCatch:
                case SlopRuleEnum.ArbitraryDelay:
                case SlopRuleEnum.WarningSuppression:
                    return SlopSeverityEnum.Warn;
                default:
                    throw new ArgumentOutOfRangeException(nameof(rule), rule, "Slop rule has no declared severity.");
            }
        }

        /// <summary>
        /// True when the content of a <c>Directory.Packages.props</c> file enables central package
        /// management.
        /// </summary>
        /// <param name="directoryPackagesProps">File content, or null when the file is absent.</param>
        /// <returns>True when central package management is enabled.</returns>
        public static bool IsCentralPackageManagementEnabled(string? directoryPackagesProps)
        {
            if (String.IsNullOrWhiteSpace(directoryPackagesProps)) return false;
            return _CentralManagementEnabled.IsMatch(directoryPackagesProps);
        }

        /// <summary>
        /// Classify the added lines of a unified diff.
        /// </summary>
        /// <param name="unifiedDiff">Output of <c>git diff</c> between the review base and the reviewed commit.</param>
        /// <param name="centralPackageManagement">
        /// Whether the repository manages package versions centrally at the reviewed commit. An inline
        /// package version is only a bypass when it is true.
        /// </param>
        /// <returns>The findings and the counts of what was examined.</returns>
        public static SlopClassificationResult Classify(string? unifiedDiff, bool centralPackageManagement)
        {
            SlopClassificationResult result = new SlopClassificationResult
            {
                CentralPackageManagement = centralPackageManagement
            };

            if (String.IsNullOrEmpty(unifiedDiff)) return result;

            foreach (DiffFile file in ParseDiff(unifiedDiff))
            {
                FileKind kind = KindOf(file.Path);
                if (kind == FileKind.Other) continue;
                if (!file.Lines.Any(line => line.Added)) continue;

                result.FilesExamined++;
                result.AddedLinesExamined += file.Lines.Count(line => line.Added);

                if (kind == FileKind.CSharp)
                    ClassifyCSharp(file, result);
                else
                    ClassifyProject(file, centralPackageManagement, result);
            }

            return result;
        }

        /// <summary>
        /// Render a classification as the text a Check output carries: one block per finding,
        /// FAIL findings first, then WARN, then suppressed.
        /// </summary>
        /// <param name="result">The classification.</param>
        /// <returns>Human-readable finding list. Never empty.</returns>
        public static string FormatFindings(SlopClassificationResult result)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("FAIL findings: " + result.FailCount + "; WARN findings: " + result.WarnCount + "; suppressed: " + result.SuppressedCount);

            if (result.Findings.Count == 0)
            {
                sb.AppendLine("No slop patterns on added lines.");
                return sb.ToString().TrimEnd();
            }

            IEnumerable<SlopFinding> ordered = result.Findings
                .Where(f => !f.Suppressed && f.Severity == SlopSeverityEnum.Fail)
                .Concat(result.Findings.Where(f => !f.Suppressed && f.Severity == SlopSeverityEnum.Warn))
                .Concat(result.Findings.Where(f => f.Suppressed));

            foreach (SlopFinding finding in ordered)
            {
                string label = finding.Suppressed
                    ? "SUPPRESSED"
                    : finding.Severity == SlopSeverityEnum.Fail ? "FAIL" : "WARN";
                sb.AppendLine();
                sb.AppendLine(label + " " + finding.Rule + " " + finding.Path + ":" + finding.Line);
                sb.AppendLine("    " + finding.Text);
                sb.AppendLine("    " + finding.Explanation);
                if (finding.Suppressed)
                    sb.AppendLine("    Recorded reason: " + finding.SuppressionReason);
                else if (!String.IsNullOrWhiteSpace(finding.SuppressionProblem))
                    sb.AppendLine("    Suppression not honored: " + finding.SuppressionProblem);
            }

            return sb.ToString().TrimEnd();
        }

        #endregion

        #region Private-Methods

        private static void ClassifyCSharp(DiffFile file, SlopClassificationResult result)
        {
            bool isTestPath = _TestPathSegment.IsMatch(file.Path);

            for (int i = 0; i < file.Lines.Count; i++)
            {
                DiffLine line = file.Lines[i];

                if (line.Added)
                {
                    string code = line.Text;

                    if (_SkipArgument.IsMatch(code) || _IgnoreAttribute.IsMatch(code) || _DynamicSkip.IsMatch(code))
                    {
                        AddFinding(file, i, i, SlopRuleEnum.SkippedTest,
                            "A test is skipped or ignored. A skipped test hides the failure it would report; fix the test or record why the skip is required.",
                            result);
                    }
                    else if (isTestPath && _IfFalse.IsMatch(code))
                    {
                        AddFinding(file, i, i, SlopRuleEnum.SkippedTest,
                            "Conditional compilation removes code from a test file, which disables the tests inside it.",
                            result);
                    }

                    if (_WarningPragma.IsMatch(code) || _SuppressMessage.IsMatch(code))
                    {
                        AddFinding(file, i, i, SlopRuleEnum.WarningSuppression,
                            "A compiler or analyzer warning is suppressed instead of fixed.",
                            result);
                    }

                    if (_LiteralDelay.IsMatch(code))
                    {
                        AddFinding(file, i, i, SlopRuleEnum.ArbitraryDelay,
                            "A literal delay usually masks a race instead of waiting for the condition it depends on.",
                            result);
                    }
                }

                if (_CatchKeyword.IsMatch(line.Text))
                {
                    int covered = MatchEmptyCatch(file.Lines, i);
                    if (covered > 0 && Enumerable.Range(i, covered).Any(index => file.Lines[index].Added))
                    {
                        AddFinding(file, i, i + covered - 1, SlopRuleEnum.EmptyCatch,
                            "An empty catch swallows the exception, so the failure it reports is never seen.",
                            result);
                    }
                }
            }
        }

        private static void ClassifyProject(DiffFile file, bool centralPackageManagement, SlopClassificationResult result)
        {
            bool isCentralPackagesFile = String.Equals(
                System.IO.Path.GetFileName(file.Path), "Directory.Packages.props", StringComparison.OrdinalIgnoreCase);

            for (int i = 0; i < file.Lines.Count; i++)
            {
                DiffLine line = file.Lines[i];
                if (!line.Added) continue;

                if (_NoWarnElement.IsMatch(line.Text))
                {
                    AddFinding(file, i, i, SlopRuleEnum.ProjectWideNoWarn,
                        "NoWarn silences the warning for every file in the project, including code written later.",
                        result);
                }

                if (!centralPackageManagement || isCentralPackagesFile) continue;

                if (_InlinePackageVersion.IsMatch(line.Text) || _VersionOverride.IsMatch(line.Text) || _CentralManagementOptOut.IsMatch(line.Text))
                {
                    AddFinding(file, i, i, SlopRuleEnum.CentralPackageVersionBypass,
                        "The repository manages package versions centrally; a version set here bypasses Directory.Packages.props.",
                        result);
                }
            }
        }

        /// <summary>
        /// Returns how many consecutive new-side lines, starting at <paramref name="start"/>, form an
        /// empty catch block, or zero when they do not.
        /// </summary>
        private static int MatchEmptyCatch(List<DiffLine> lines, int start)
        {
            StringBuilder window = new StringBuilder();
            for (int count = 1; count <= _MaxCatchWindowLines && start + count - 1 < lines.Count; count++)
            {
                int index = start + count - 1;
                if (count > 1 && lines[index].Number != lines[index - 1].Number + 1) return 0;

                window.Append(lines[index].Text).Append('\n');
                string code = _LineComment.Replace(_BlockComment.Replace(window.ToString(), " "), " ");
                Match match = _EmptyCatch.Match(code);
                if (match.Success) return count;

                // Once the block has opened and closed with content, a longer window cannot make it empty.
                int open = code.IndexOf('{', StringComparison.Ordinal);
                if (open >= 0 && code.IndexOf('}', open) > open) return 0;
            }

            return 0;
        }

        private static void AddFinding(
            DiffFile file,
            int firstIndex,
            int lastIndex,
            SlopRuleEnum rule,
            string explanation,
            SlopClassificationResult result)
        {
            SlopFinding finding = new SlopFinding
            {
                Rule = rule,
                Severity = SeverityOf(rule),
                Path = file.Path,
                Line = file.Lines[firstIndex].Number,
                Text = file.Lines[firstIndex].Text.Trim(),
                Explanation = explanation
            };

            ApplySuppression(file, firstIndex, lastIndex, finding);
            result.Findings.Add(finding);
        }

        private static void ApplySuppression(DiffFile file, int firstIndex, int lastIndex, SlopFinding finding)
        {
            List<int> candidates = new List<int>();
            for (int index = firstIndex; index <= lastIndex; index++) candidates.Add(index);
            if (firstIndex > 0 && file.Lines[firstIndex - 1].Number == file.Lines[firstIndex].Number - 1)
                candidates.Add(firstIndex - 1);

            foreach (int index in candidates)
            {
                Match match = _Marker.Match(file.Lines[index].Text);
                if (!match.Success) continue;

                string ruleName = match.Groups[1].Value;
                string reason = match.Groups[2].Value.Trim();

                if (!String.Equals(ruleName, finding.Rule.ToString(), StringComparison.OrdinalIgnoreCase))
                {
                    finding.SuppressionProblem ??= "the marker names " + ruleName + ", not " + finding.Rule + ".";
                    continue;
                }

                if (reason.Length < MinimumReasonLength)
                {
                    finding.SuppressionProblem = "the marker records no reason (at least " + MinimumReasonLength + " characters are required).";
                    continue;
                }

                finding.Suppressed = true;
                finding.SuppressionReason = reason;
                finding.SuppressionProblem = null;
                return;
            }
        }

        private static FileKind KindOf(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) return FileKind.Other;

            string normalized = "/" + path.Replace('\\', '/');
            if (normalized.Contains("/bin/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/obj/", StringComparison.OrdinalIgnoreCase))
                return FileKind.Other;

            string extension = System.IO.Path.GetExtension(path);
            if (String.Equals(extension, ".cs", StringComparison.OrdinalIgnoreCase)) return FileKind.CSharp;
            if (String.Equals(extension, ".csproj", StringComparison.OrdinalIgnoreCase)
                || String.Equals(extension, ".fsproj", StringComparison.OrdinalIgnoreCase)
                || String.Equals(extension, ".vbproj", StringComparison.OrdinalIgnoreCase)
                || String.Equals(extension, ".props", StringComparison.OrdinalIgnoreCase)
                || String.Equals(extension, ".targets", StringComparison.OrdinalIgnoreCase))
                return FileKind.Project;

            return FileKind.Other;
        }

        private static List<DiffFile> ParseDiff(string unifiedDiff)
        {
            List<DiffFile> files = new List<DiffFile>();
            DiffFile? current = null;
            bool inHeader = false;
            int newLine = 0;

            using (StringReader reader = new StringReader(unifiedDiff))
            {
                string? raw;
                while ((raw = reader.ReadLine()) != null)
                {
                    if (raw.StartsWith("diff --git ", StringComparison.Ordinal))
                    {
                        current = new DiffFile();
                        files.Add(current);
                        inHeader = true;
                        continue;
                    }

                    if (current == null) continue;

                    Match hunk = _HunkHeader.Match(raw);
                    if (hunk.Success)
                    {
                        inHeader = false;
                        newLine = Int32.Parse(hunk.Groups[1].Value);
                        continue;
                    }

                    if (inHeader)
                    {
                        if (raw.StartsWith("+++ ", StringComparison.Ordinal))
                            current.Path = ParseNewPath(raw.Substring(4));
                        continue;
                    }

                    if (raw.StartsWith("+", StringComparison.Ordinal))
                    {
                        current.Lines.Add(new DiffLine(newLine, raw.Substring(1), true));
                        newLine++;
                    }
                    else if (raw.StartsWith(" ", StringComparison.Ordinal))
                    {
                        current.Lines.Add(new DiffLine(newLine, raw.Substring(1), false));
                        newLine++;
                    }
                    else if (raw.Length == 0)
                    {
                        current.Lines.Add(new DiffLine(newLine, String.Empty, false));
                        newLine++;
                    }
                }
            }

            return files.Where(file => !String.IsNullOrWhiteSpace(file.Path)).ToList();
        }

        private static string ParseNewPath(string value)
        {
            string path = value.TrimEnd('\t', ' ');
            if (path.Length >= 2 && path[0] == '"' && path[path.Length - 1] == '"')
                path = path.Substring(1, path.Length - 2);
            if (String.Equals(path, "/dev/null", StringComparison.Ordinal)) return String.Empty;
            if (path.StartsWith("b/", StringComparison.Ordinal)) path = path.Substring(2);
            return path;
        }

        #endregion

        #region Private-Types

        private enum FileKind
        {
            Other,
            CSharp,
            Project
        }

        private sealed class DiffFile
        {
            public string Path { get; set; } = String.Empty;
            public List<DiffLine> Lines { get; } = new List<DiffLine>();
        }

        private sealed class DiffLine
        {
            public DiffLine(int number, string text, bool added)
            {
                Number = number;
                Text = text;
                Added = added;
            }

            public int Number { get; }
            public string Text { get; }
            public bool Added { get; }
        }

        #endregion
    }
}
