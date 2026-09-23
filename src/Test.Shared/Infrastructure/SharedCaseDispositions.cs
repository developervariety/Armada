namespace Test.Shared.Infrastructure
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using Touchstone.Core;

    /// <summary>
    /// The one registry of shared cases that do not execute in the shared runner. Every entry names why and,
    /// for a duplicate, the executed legacy case that owns the behaviour. Discovery applies it to every
    /// runner, so each such case reports as a named, counted skip with that reason. An entry that names no
    /// discovered case, or a legacy owner that no longer exists, fails discovery instead of hiding a case.
    /// </summary>
    public static class SharedCaseDispositions
    {
        #region Public-Members

        /// <summary>
        /// Every recorded disposition.
        /// </summary>
        public static IReadOnlyList<SharedCaseDisposition> All
        {
            get { return _All; }
        }

        #endregion

        #region Private-Members

        private static readonly IReadOnlyList<SharedCaseDisposition> _All = new List<SharedCaseDisposition>
        {
        };

        #endregion

        #region Public-Methods

        /// <summary>
        /// Apply dispositions to discovered suites. A case named by a disposition becomes a skip carrying the
        /// disposition's reason; every other case is unchanged.
        /// </summary>
        /// <param name="suites">Discovered suites.</param>
        /// <param name="dispositions">Dispositions to apply.</param>
        /// <param name="repositoryRoot">Repository root used to verify legacy owners, or null when the sources are not present.</param>
        /// <returns>The suites with dispositions applied.</returns>
        /// <exception cref="InvalidOperationException">A disposition is repeated, names no discovered case, or names a legacy owner that does not exist.</exception>
        public static IReadOnlyList<TestSuiteDescriptor> Apply(
            IReadOnlyList<TestSuiteDescriptor> suites,
            IReadOnlyList<SharedCaseDisposition> dispositions,
            string? repositoryRoot)
        {
            if (suites == null) throw new ArgumentNullException(nameof(suites));
            if (dispositions == null) throw new ArgumentNullException(nameof(dispositions));

            List<string> problems = new List<string>();
            Dictionary<string, SharedCaseDisposition> byTestId = new Dictionary<string, SharedCaseDisposition>(StringComparer.Ordinal);
            foreach (SharedCaseDisposition disposition in dispositions)
            {
                if (!byTestId.TryAdd(disposition.TestId, disposition))
                    problems.Add(disposition.TestId + " is recorded more than once");
            }

            HashSet<string> matched = new HashSet<string>(StringComparer.Ordinal);
            List<TestSuiteDescriptor> result = new List<TestSuiteDescriptor>();
            foreach (TestSuiteDescriptor suite in suites)
            {
                List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();
                foreach (TestCaseDescriptor testCase in suite.Cases)
                {
                    if (!byTestId.TryGetValue(testCase.TestId, out SharedCaseDisposition? disposition) || testCase.Skip)
                    {
                        cases.Add(testCase);
                        continue;
                    }

                    matched.Add(testCase.TestId);
                    cases.Add(new TestCaseDescriptor(
                        suiteId: testCase.SuiteId,
                        caseId: testCase.CaseId,
                        displayName: testCase.DisplayName,
                        executeAsync: testCase.ExecuteAsync,
                        tags: testCase.Tags,
                        skip: true,
                        skipReason: disposition.SkipReason));
                }

                result.Add(new TestSuiteDescriptor(suite.SuiteId, suite.DisplayName, cases, suite.BeforeSuiteAsync, suite.AfterSuiteAsync));
            }

            foreach (SharedCaseDisposition disposition in byTestId.Values)
            {
                if (!matched.Contains(disposition.TestId)
                    && !suites.Any(suite => suite.Cases.Any(testCase => testCase.TestId == disposition.TestId)))
                {
                    problems.Add(disposition.TestId + " names no discovered case");
                }

                if (disposition.Kind != SharedCaseDispositionKindEnum.DuplicateOfLegacyCase || repositoryRoot == null) continue;

                string ownerPath = Path.Combine(repositoryRoot, disposition.OwnerFile!);
                if (!File.Exists(ownerPath))
                    problems.Add(disposition.TestId + " names legacy owner file " + disposition.OwnerFile + ", which does not exist");
                else if (!File.ReadAllText(ownerPath).Contains("\"" + disposition.OwnerCase + "\"", StringComparison.Ordinal))
                    problems.Add(disposition.TestId + " names legacy owner case '" + disposition.OwnerCase + "', which " + disposition.OwnerFile + " does not register");
            }

            if (problems.Count > 0)
                throw new InvalidOperationException("Shared case dispositions are stale: " + String.Join("; ", problems));

            return result;
        }

        /// <summary>
        /// Locate the repository root by walking up from the runner's base directory to the directory holding
        /// <c>src/Armada.sln</c>.
        /// </summary>
        /// <returns>The repository root, or null when the runner executes outside a source checkout.</returns>
        public static string? FindRepositoryRoot()
        {
            DirectoryInfo? directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "src", "Armada.sln"))) return directory.FullName;
                directory = directory.Parent;
            }

            return null;
        }

        #endregion
    }
}
