namespace Armada.Test.Automated
{
    using System.Threading.Tasks;
    using Armada.Test.Shared;
    using Touchstone.Cli;

    /// <summary>
    /// Console/CLI runner for the shared Armada Touchstone test descriptors. Runs every
    /// discovered suite via the Touchstone console runner, printing colored tabular results
    /// and returning a non-zero exit code if any test fails. Suites can be narrowed for local
    /// runs with the ARMADA_TEST_SUITES environment variable (comma-separated suiteId prefixes,
    /// e.g. ARMADA_TEST_SUITES=E2E,Database).
    /// </summary>
    public static class Program
    {
        /// <summary>
        /// Entry point. Optionally accepts <c>--results &lt;path&gt;</c> to export JSON results.
        /// </summary>
        /// <param name="args">Command-line arguments.</param>
        /// <returns>Process exit code: 0 when all tests pass, non-zero otherwise.</returns>
        public static async Task<int> Main(string[] args)
        {
            string? resultsPath = null;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--results" && i + 1 < args.Length)
                {
                    resultsPath = args[i + 1];
                    break;
                }
            }

            return await ConsoleRunner.RunAsync(ArmadaTestSuites.All, resultsPath: resultsPath).ConfigureAwait(false);
        }
    }
}
