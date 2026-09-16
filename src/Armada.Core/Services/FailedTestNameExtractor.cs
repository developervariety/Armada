namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Text.RegularExpressions;

    /// <summary>
    /// Extracts the identifiers of the tests a runner reported as failed, from the still-whole
    /// combined output of a failed test command. The result is an ordered, de-duplicated set plus
    /// an explicit overflow flag: only a complete, non-overflowed set is comparable between two
    /// runs, so a caller that compares sets must treat an overflowed set as unknown. Line shapes
    /// differ by runner, so each runner has its own line matcher; a line that no matcher recognizes
    /// contributes no name.
    /// </summary>
    public sealed class FailedTestNameExtractor
    {
        #region Public-Members

        /// <summary>
        /// Largest number of distinct failing test identifiers a set keeps. A run that names more
        /// than this many distinct failing tests sets the overflow flag, because the persisted set
        /// can no longer be proven complete and so is not comparable.
        /// </summary>
        public const int MaxNames = 200;

        #endregion

        #region Private-Members

        // dotnet VSTest per-test failure line: leading whitespace, the word "Failed" followed by
        // whitespace (so the "Failed!  - Failed: N" summary, which has no space after "Failed", is
        // never matched), the fully-qualified test name, then an optional "[<duration>]" marker.
        // Parameterized cases carry their arguments in parentheses inside the name and are kept
        // verbatim, so two runs of the same case produce the same identifier.
        private static readonly Regex _DotnetFailedLinePattern = new Regex(
            @"^[ \t]*Failed[ \t]+(?<name>\S.*?)(?:[ \t]+\[[^\]]*\])?[ \t]*$",
            RegexOptions.Compiled);

        // Python unittest verbose failure header: "FAIL:" or "ERROR:" (an errored test is a failed
        // test for this purpose), the test method, then an optional "(context)". Case-sensitive so
        // a "Error Message:" line from a dotnet run is never read as a python failure.
        private static readonly Regex _PythonUnittestLinePattern = new Regex(
            @"^(?:FAIL|ERROR):[ \t]+(?<method>[^\s(]+)(?:[ \t]+\((?<context>[^)]*)\))?[ \t]*$",
            RegexOptions.Compiled);

        // pytest short-summary failure line: "FAILED" / "ERROR" then a node id of the form
        // "path::test". Kept case-sensitive to match pytest's own upper-case summary tokens.
        private static readonly Regex _PytestLinePattern = new Regex(
            @"^(?:FAILED|ERROR)[ \t]+(?<name>\S+::\S+?)(?:[ \t]+.*)?$",
            RegexOptions.Compiled);

        #endregion

        #region Public-Methods

        /// <summary>
        /// Read the failing test identifiers from a runner's combined output.
        /// </summary>
        /// <param name="output">Whole combined standard output and standard error of the failed command.</param>
        /// <returns>The ordered, de-duplicated set of failing test identifiers and its overflow flag.</returns>
        public static FailedTestNameSet Extract(string? output)
        {
            if (String.IsNullOrEmpty(output))
                return FailedTestNameSet.Empty;

            List<string> ordered = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            bool overflow = false;

            string[] lines = output.Replace("\r\n", "\n").Split('\n');
            foreach (string rawLine in lines)
            {
                string line = rawLine.TrimEnd('\r');
                string? name = MatchLine(line);
                if (name == null) continue;

                if (!seen.Add(name)) continue;

                if (ordered.Count >= MaxNames)
                {
                    // A distinct failing test the set can no longer keep. The set is now incomplete,
                    // so it is not comparable and the caller must treat it as unknown.
                    overflow = true;
                    break;
                }

                ordered.Add(name);
            }

            return new FailedTestNameSet(ordered, overflow);
        }

        #endregion

        #region Private-Methods

        private static string? MatchLine(string line)
        {
            if (String.IsNullOrWhiteSpace(line)) return null;

            Match dotnet = _DotnetFailedLinePattern.Match(line);
            if (dotnet.Success)
            {
                string name = dotnet.Groups["name"].Value.Trim();
                return name.Length == 0 ? null : name;
            }

            Match pytest = _PytestLinePattern.Match(line);
            if (pytest.Success)
            {
                string name = pytest.Groups["name"].Value.Trim();
                return name.Length == 0 ? null : name;
            }

            Match python = _PythonUnittestLinePattern.Match(line);
            if (python.Success)
                return NormalizePythonName(python.Groups["method"].Value.Trim(), python.Groups["context"].Value.Trim());

            return null;
        }

        // Produce one stable identifier from a unittest header. The classic form
        // "FAIL: test_x (module.Case)" and the newer "FAIL: test_x (module.Case.test_x)" both
        // resolve to "module.Case.test_x", so two runs printing either form compare equal.
        private static string? NormalizePythonName(string method, string context)
        {
            if (method.Length == 0) return null;
            if (context.Length == 0) return method;

            if (String.Equals(context, method, StringComparison.Ordinal)) return context;
            if (context.EndsWith("." + method, StringComparison.Ordinal)) return context;
            return context + "." + method;
        }

        #endregion

        #region Public-Classes

        /// <summary>
        /// An ordered, de-duplicated set of failing test identifiers and whether it overflowed. An
        /// overflowed set is incomplete and must be treated as unknown by any comparison.
        /// </summary>
        public readonly struct FailedTestNameSet
        {
            /// <summary>
            /// An empty, non-overflowed set.
            /// </summary>
            public static readonly FailedTestNameSet Empty = new FailedTestNameSet(new List<string>(), false);

            /// <summary>
            /// Instantiate.
            /// </summary>
            /// <param name="names">Ordered, de-duplicated failing test identifiers.</param>
            /// <param name="overflow">True when the set is incomplete.</param>
            public FailedTestNameSet(IReadOnlyList<string> names, bool overflow)
            {
                Names = names ?? throw new ArgumentNullException(nameof(names));
                Overflow = overflow;
            }

            /// <summary>
            /// Ordered, de-duplicated failing test identifiers.
            /// </summary>
            public IReadOnlyList<string> Names { get; }

            /// <summary>
            /// True when the runner named more distinct failing tests than the set could keep, so
            /// the set is incomplete and not comparable.
            /// </summary>
            public bool Overflow { get; }
        }

        #endregion
    }
}
