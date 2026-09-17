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
        private TestShard? _Shard = null;

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

        /// <summary>Registered suite names in registration order.</summary>
        public List<string> GetSuiteNames()
        {
            return _Suites.Select(suite => suite.Name).ToList();
        }

        /// <summary>
        /// Print the names of the suites a run with the same shard would execute, one per line, and return the
        /// exit code (0 = listed, 1 = the shard plan is invalid).
        /// </summary>
        public int ListSuites(TestShard? shard = null, SuiteShardPlan? plan = null)
        {
            List<TestSuite> suites;
            try
            {
                suites = SelectShard(shard, plan);
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is ArgumentException)
            {
                Console.Error.WriteLine(ex.Message);
                return 1;
            }

            foreach (TestSuite suite in suites) Console.WriteLine(suite.Name);
            return 0;
        }

        /// <summary>
        /// Run all suites sequentially, print results, and return the exit code (0 = pass, 1 = fail).
        /// </summary>
        public Task<int> RunAllAsync(IEnumerable<string>? suiteFilters = null)
        {
            return RunAllAsync(suiteFilters, null, null);
        }

        /// <summary>
        /// Run the suites selected by the filters and, when a shard is given, only the suites the plan assigns
        /// to that shard. A shard that is assigned no suites fails like any other empty selection.
        /// </summary>
        public async Task<int> RunAllAsync(IEnumerable<string>? suiteFilters, TestShard? shard, SuiteShardPlan? plan)
        {
            _AllResults.Clear();
            _Shard = shard;
            Console.WriteLine();
            Console.WriteLine("================================================================================");
            Console.WriteLine(_Title);
            Console.WriteLine("================================================================================");

            Stopwatch totalTimer = Stopwatch.StartNew();
            List<string> filters = suiteFilters?.ToList() ?? new List<string>();
            if (shard != null && filters.Count > 0)
            {
                Console.Error.WriteLine("A shard cannot be combined with suite filters.");
                WriteManifest(filters, new List<TestSuite>(), 0, "Shard combined with suite filters");
                return 1;
            }

            foreach (string filter in filters)
            {
                if (String.IsNullOrWhiteSpace(filter) || !_Suites.Any(suite => Matches(suite, filter.Trim())))
                {
                    Console.Error.WriteLine("Invalid or unmatched suite filter: " + filter);
                    WriteManifest(filters, new List<TestSuite>(), 0, "Invalid or unmatched suite filter");
                    return 1;
                }
            }

            List<TestSuite> suites;
            try
            {
                suites = SelectShard(shard, plan)
                    .Where(suite => filters.Count == 0 || filters.Any(filter => Matches(suite, filter.Trim())))
                    .ToList();
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is ArgumentException)
            {
                Console.Error.WriteLine("Invalid shard plan: " + ex.Message);
                WriteManifest(filters, new List<TestSuite>(), 0, "Invalid shard plan");
                return 1;
            }

            if (shard != null)
            {
                // The combined runner sums these counts across shards and fails when they do not add up to the
                // registered total, so a suite dropped or duplicated by the split cannot pass unnoticed.
                Console.WriteLine("Shard " + shard + ": " + suites.Count + " of " + _Suites.Count + " suites");
            }

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
            string fileName = _Shard == null ? executable : executable + ".shard-" + _Shard.Index + "-of-" + _Shard.Count;
            File.WriteAllText(Path.Combine(directory, fileName + ".json"), json);
        }

        private List<TestSuite> SelectShard(TestShard? shard, SuiteShardPlan? plan)
        {
            if (shard == null) return _Suites.ToList();
            if (plan == null) throw new InvalidOperationException("A shard run requires a shard plan.");
            List<List<string>> assignment = plan.Assign(GetSuiteNames(), shard.Count);
            HashSet<string> names = new HashSet<string>(assignment[shard.Index - 1], StringComparer.Ordinal);
            return _Suites.Where(suite => names.Contains(suite.Name)).ToList();
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
