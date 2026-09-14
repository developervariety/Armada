namespace Test.Automated
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading.Tasks;
    using Test.Shared;
    using Test.Shared.Infrastructure;
    using Touchstone.Cli;
    using Touchstone.Core;

    /// <summary>
    /// Console/CLI runner for the shared Armada Touchstone test descriptors. Runs every discovered suite via
    /// the Touchstone console runner, printing colored tabular results and returning a non-zero exit code if
    /// any test fails.
    ///
    /// Suites can be narrowed with the ARMADA_TEST_SUITES environment variable (comma-separated suiteId
    /// prefixes, e.g. ARMADA_TEST_SUITES=E2E,Database), or with --suites on the command line.
    ///
    /// The database provider can be selected with --db-type (postgresql | mysql | sqlserver | sqlite) plus
    /// --db-host, --db-port, --db-user, --db-pass, and --db-name. These map onto the ARMADA_TEST_DB_*
    /// environment variables the shared harness reads, so the same suite runs unchanged against a real
    /// server. SQLite (the default) needs no connection arguments.
    ///
    /// After the run it lists every skipped case with its reason and the disposition counts. Discovery that
    /// fails, a filter that matches no suite, or a selection with nothing to execute exits with code 2.
    /// </summary>
    public static class Program
    {
        /// <summary>
        /// Entry point. Accepts <c>--results &lt;path&gt;</c>, <c>--suites &lt;prefixes&gt;</c>, and the
        /// <c>--db-*</c> connection arguments described on the class.
        /// </summary>
        /// <param name="args">Command-line arguments.</param>
        /// <returns>Process exit code: 0 when all tests pass, non-zero otherwise.</returns>
        public static async Task<int> Main(string[] args)
        {
            string? resultsPath = null;
            TestProcessEnvironment.RemoveProviderVariablesAndReport();

            IReadOnlyCollection<Armada.Core.Enums.AgentRuntimeEnum> realRuntimes;
            try
            {
                realRuntimes = TestAgentRuntimeFactory.ReadOptedInRuntimes();
            }
            catch (ArgumentException ex)
            {
                Console.Error.WriteLine("RESULT: FAIL (configuration) " + ex.Message);
                return 2;
            }

            for (int i = 0; i < args.Length; i++)
            {
                string current = args[i];
                string? next = i + 1 < args.Length ? args[i + 1] : null;

                switch (current)
                {
                    case "--results":
                        if (next != null) resultsPath = next;
                        break;
                    case "--suites":
                        if (next != null) Environment.SetEnvironmentVariable("ARMADA_TEST_SUITES", next);
                        break;
                    case "--db-type":
                        if (next != null) Environment.SetEnvironmentVariable("ARMADA_TEST_DB_TYPE", next);
                        break;
                    case "--db-host":
                        if (next != null) Environment.SetEnvironmentVariable("ARMADA_TEST_DB_HOST", next);
                        break;
                    case "--db-port":
                        if (next != null) Environment.SetEnvironmentVariable("ARMADA_TEST_DB_PORT", next);
                        break;
                    case "--db-user":
                        if (next != null) Environment.SetEnvironmentVariable("ARMADA_TEST_DB_USER", next);
                        break;
                    case "--db-pass":
                        if (next != null) Environment.SetEnvironmentVariable("ARMADA_TEST_DB_PASS", next);
                        break;
                    case "--db-name":
                        if (next != null) Environment.SetEnvironmentVariable("ARMADA_TEST_DB_NAME", next);
                        break;
                    default:
                        break;
                }
            }

            IReadOnlyList<TestSuiteDescriptor> suites;
            try
            {
                suites = ArmadaTestSuites.All;
            }
            catch (InvalidOperationException ex)
            {
                Console.Error.WriteLine("RESULT: FAIL (discovery) " + ex.Message);
                return 2;
            }

            if (suites.Count == 0)
            {
                Console.Error.WriteLine("RESULT: FAIL (discovery) no suite matched the suite filter '"
                    + Environment.GetEnvironmentVariable("ARMADA_TEST_SUITES") + "'.");
                return 2;
            }

            List<TestCaseDescriptor> skipped = suites.SelectMany(suite => suite.Cases).Where(testCase => testCase.Skip).ToList();
            int executable = suites.Sum(suite => suite.Cases.Count) - skipped.Count;
            if (executable == 0)
            {
                Console.Error.WriteLine("RESULT: FAIL (discovery) every selected case is skipped, so the run would execute nothing.");
                return 2;
            }

            int exitCode = await ConsoleRunner.RunAsync(suites, resultsPath: resultsPath).ConfigureAwait(false);

            WriteSkipped(skipped);

            string? launchFailure = TestProcessLaunchLog.DescribeUnpermittedProcessLaunches(realRuntimes);
            if (launchFailure != null)
            {
                Console.WriteLine("RESULT: FAIL (agent process launches)");
                Console.WriteLine(launchFailure);
                return exitCode == 0 ? 1 : exitCode;
            }

            return exitCode;
        }

        private static void WriteSkipped(List<TestCaseDescriptor> skipped)
        {
            Console.WriteLine("Skipped Tests: " + skipped.Count);
            foreach (TestCaseDescriptor testCase in skipped)
            {
                Console.WriteLine("  " + testCase.TestId);
                Console.WriteLine("    " + (String.IsNullOrWhiteSpace(testCase.SkipReason) ? "(no reason recorded)" : testCase.SkipReason));
            }

            HashSet<string> skippedIds = new HashSet<string>(skipped.Select(testCase => testCase.TestId), StringComparer.Ordinal);
            int duplicates = SharedCaseDispositions.All.Count(d => d.Kind == SharedCaseDispositionKindEnum.DuplicateOfLegacyCase && skippedIds.Contains(d.TestId));
            int awaiting = SharedCaseDispositions.All.Count(d => d.Kind == SharedCaseDispositionKindEnum.AwaitingOwnerDecision && skippedIds.Contains(d.TestId));
            Console.WriteLine("Skipped by disposition: " + duplicates + " duplicate of an executed legacy case, " + awaiting + " awaiting owner decision, "
                + (skipped.Count - duplicates - awaiting) + " other named skip.");
            if (SharedCaseDispositions.FindRepositoryRoot() == null)
                Console.WriteLine("Legacy owners were not verified: the runner is not inside a source checkout.");
        }
    }
}
