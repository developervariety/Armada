namespace Armada.Test.Common
{
    using System.Diagnostics;

    /// <summary>
    /// Orchestrates test suite execution, collects results, and prints a summary.
    /// </summary>
    public class TestRunner
    {
        #region Private-Members

        private List<TestSuite> _Suites = new List<TestSuite>();
        private List<TestResult> _AllResults = new List<TestResult>();
        private string _Title;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Create a new test runner with the given title.
        /// </summary>
        public TestRunner(string title)
        {
            _Title = title ?? throw new ArgumentNullException(nameof(title));
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Add a test suite to the runner.
        /// </summary>
        public void AddSuite(TestSuite suite)
        {
            ArgumentNullException.ThrowIfNull(suite);
            if (_Suites.Any(existing => existing.GetType() == suite.GetType()))
                throw new ArgumentException("Duplicate suite type: " + suite.GetType().FullName, nameof(suite));
            _Suites.Add(suite);
        }

        /// <summary>Fail if a compiled public suite is absent from the explicit fixture registry.</summary>
        public void VerifyRegistration(System.Reflection.Assembly assembly)
        {
            VerifyRegistration(assembly.GetTypes().Where(type => type.IsPublic && !type.IsAbstract
                && typeof(TestSuite).IsAssignableFrom(type)));
        }

        /// <summary>Verify an explicitly discovered set without constructing dependency-bearing suites.</summary>
        public void VerifyRegistration(IEnumerable<Type> discovered)
        {
            HashSet<Type> expected = new HashSet<Type>(discovered);
            HashSet<Type> registered = new HashSet<Type>(_Suites.Select(suite => suite.GetType()));
            if (!expected.SetEquals(registered))
                throw new InvalidOperationException("Suite registration differs from compiled discovery. Missing: "
                    + String.Join(", ", expected.Except(registered).Select(type => type.FullName))
                    + "; unexpected: " + String.Join(", ", registered.Except(expected).Select(type => type.FullName)));
        }

        /// <summary>
        /// Run all suites sequentially, print results, and return the exit code (0 = pass, 1 = fail).
        /// </summary>
        public async Task<int> RunAllAsync(IEnumerable<string>? suiteFilters = null)
        {
            _AllResults.Clear();
            Console.WriteLine();
            Console.WriteLine("================================================================================");
            Console.WriteLine(_Title);
            Console.WriteLine("================================================================================");

            Stopwatch totalTimer = Stopwatch.StartNew();
            List<string> filters = suiteFilters?.ToList() ?? new List<string>();
            foreach (string filter in filters)
            {
                if (String.IsNullOrWhiteSpace(filter) || !_Suites.Any(suite => Matches(suite, filter.Trim())))
                {
                    Console.Error.WriteLine("Invalid or unmatched suite filter: " + filter);
                    WriteManifest(filters, new List<TestSuite>(), 0, "Invalid or unmatched suite filter");
                    return 1;
                }
            }
            List<TestSuite> suites = _Suites
                .Where(suite => filters.Count == 0 || filters.Any(filter => Matches(suite, filter.Trim())))
                .ToList();
            if (suites.Count == 0)
            {
                Console.Error.WriteLine("No test suites were selected.");
                WriteManifest(filters, new List<TestSuite>(), 0, "No test suites selected");
                return 1;
            }

            foreach (TestSuite suite in suites)
            {
                Console.WriteLine();
                Console.WriteLine("--- " + suite.Name + " ---");

                List<TestResult> results = await suite.RunAsync().ConfigureAwait(false);
                if (results.Count == 0)
                    results.Add(new TestResult { Name = suite.Name + " [empty suite]", SuiteId = suite.GetType().FullName ?? suite.GetType().Name, Message = "Suite executed zero cases." });
                _AllResults.AddRange(results);
            }

            totalTimer.Stop();
            WriteManifest(filters, suites, totalTimer.ElapsedMilliseconds);

            return PrintSummary(totalTimer.ElapsedMilliseconds);
        }

        #endregion

        #region Private-Methods

        private void WriteManifest(List<string> filters, List<TestSuite> selected, long elapsedMs, string? runError = null)
        {
            string? directory = Environment.GetEnvironmentVariable("ARMADA_TEST_RESULTS_DIRECTORY");
            if (String.IsNullOrWhiteSpace(directory)) return;
            Directory.CreateDirectory(directory);
            string executable = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name ?? "unknown";
            string json = System.Text.Json.JsonSerializer.Serialize(new
            {
                SchemaVersion = 1,
                ExecutableSha256 = TestBuildEvidence.ReadExecutableChecksum(),
                BuildSources = TestBuildEvidence.ReadSourceChecksums(),
                RunId = Guid.NewGuid().ToString("N"),
                RunError = runError,
                Executable = executable,
                Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                DurationMs = elapsedMs,
                RequestedFilters = filters,
                RegisteredSuites = _Suites.Select(suite => new { SuiteId = suite.GetType().FullName, suite.Name,
                    Selected = selected.Contains(suite), Disposition = selected.Contains(suite) ? "selected" : "filtered" }),
                Cases = _AllResults.Select(result => new { result.SuiteId, CaseId = result.Name,
                    result.SourcePath, result.SourceLine, Outcome = result.SkipReason != null ? "skipped" : result.Passed ? "passed" : "failed", result.SkipReason, result.ElapsedMs })
            }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(directory, executable + ".json"), json);
        }

        private static bool Matches(TestSuite suite, string filter)
        {
            return suite.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0
                || suite.GetType().Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private int PrintSummary(long totalMs)
        {
            int total = _AllResults.Count;
            int passed = _AllResults.Count(r => r.Passed);
            int skipped = _AllResults.Count(r => r.SkipReason != null);
            int failed = total - passed - skipped;
            bool success = failed == 0 && passed > 0;

            List<TestResult> failedTests = _AllResults.Where(r => !r.Passed && r.SkipReason == null).ToList();

            Console.WriteLine();
            Console.WriteLine("================================================================================");
            Console.WriteLine("TEST SUMMARY");
            Console.WriteLine("================================================================================");
            Console.WriteLine("Total: " + total + "  Passed: " + passed + "  Failed: " + failed + "  Skipped: " + skipped + "  Runtime: " + totalMs + "ms");

            if (failedTests.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("Failed Tests:");
                foreach (TestResult result in failedTests)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.Write("  - " + result.Name);
                    Console.ResetColor();
                    Console.WriteLine(": " + result.Message);
                }
            }

            Console.WriteLine();
            Console.WriteLine("================================================================================");

            if (success)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("RESULT: PASS");
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("RESULT: FAIL");
            }

            Console.ResetColor();
            Console.WriteLine("================================================================================");
            Console.WriteLine();

            return success ? 0 : 1;
        }

        #endregion
    }
}
