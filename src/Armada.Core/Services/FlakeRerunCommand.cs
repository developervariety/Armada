namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Text;

    /// <summary>
    /// Builds the isolated, class-filtered re-run command the D15 <c>flake_score</c> gate runs when a
    /// red unit-test result reads as a load flake or a known flaky family. The re-run narrows the
    /// vessel's own test command to only the failing test classes, so a genuine flake passes alone while
    /// a real defect fails alone — and either way the re-run's real result is the truth. The builder is
    /// deliberately conservative: it forms a command ONLY for a <c>dotnet test</c> command that does not
    /// already carry a filter, and returns false otherwise, so the gate never fabricates a green result
    /// from a command it could not isolate.
    /// </summary>
    public static class FlakeRerunCommand
    {
        #region Public-Methods

        /// <summary>
        /// Derive the distinct simple class names from a set of fully-qualified failing test names.
        /// </summary>
        /// <remarks>
        /// A dotnet failing test identifier is the fully-qualified test name, for example
        /// <c>Ns.Sub.ClassName.MethodName</c>, optionally with parameterized arguments in parentheses.
        /// The class name is the last dotted segment before the method, with any argument tail removed.
        /// </remarks>
        /// <param name="failingTestNames">The failing test identifiers.</param>
        /// <returns>The ordered, de-duplicated simple class names.</returns>
        public static IReadOnlyList<string> DeriveClassNames(IEnumerable<string>? failingTestNames)
        {
            List<string> ordered = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            if (failingTestNames == null) return ordered;

            foreach (string rawName in failingTestNames)
            {
                if (String.IsNullOrWhiteSpace(rawName)) continue;

                // Drop any parameterized-argument tail so the class name is stable across runs.
                string name = rawName.Trim();
                int paren = name.IndexOf('(');
                if (paren >= 0) name = name.Substring(0, paren);
                name = name.TrimEnd('.');

                string[] segments = name.Split('.', StringSplitOptions.RemoveEmptyEntries);
                if (segments.Length < 2) continue; // Need at least a class and a method segment.

                string className = segments[segments.Length - 2];
                if (className.Length == 0) continue;
                if (seen.Add(className)) ordered.Add(className);
            }

            return ordered;
        }

        /// <summary>
        /// Try to build an isolated class-filtered re-run command from the vessel's test command and the
        /// failing test classes. Succeeds only for a <c>dotnet test</c> command that has no existing
        /// <c>--filter</c>; returns false otherwise so the gate declines to re-run rather than run a
        /// command that would not actually isolate the failing classes.
        /// </summary>
        /// <param name="testCommand">The vessel's unit-test command.</param>
        /// <param name="classNames">The failing test class names to isolate.</param>
        /// <param name="filteredCommand">The isolated command, when one could be built.</param>
        /// <returns>True when an isolated command was built.</returns>
        public static bool TryBuild(string? testCommand, IReadOnlyList<string> classNames, out string filteredCommand)
        {
            filteredCommand = String.Empty;
            if (String.IsNullOrWhiteSpace(testCommand) || classNames == null || classNames.Count == 0) return false;

            // Only a `dotnet test` command supports the class filter used here, and only when it does not
            // already carry a filter that this one would have to reconcile with.
            if (testCommand.IndexOf("dotnet test", StringComparison.OrdinalIgnoreCase) < 0) return false;
            if (testCommand.IndexOf("--filter", StringComparison.OrdinalIgnoreCase) >= 0) return false;

            StringBuilder filter = new StringBuilder();
            foreach (string className in classNames)
            {
                if (String.IsNullOrWhiteSpace(className)) continue;
                if (filter.Length > 0) filter.Append('|');
                filter.Append("FullyQualifiedName~").Append(className);
            }
            if (filter.Length == 0) return false;

            filteredCommand = testCommand.TrimEnd() + " --filter \"" + filter.ToString() + "\"";
            return true;
        }

        #endregion
    }
}
