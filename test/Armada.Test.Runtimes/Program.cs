namespace Armada.Test.Runtimes
{
    using Armada.Test.Common;
    using Armada.Test.Runtimes.Suites;

    public class Program
    {
        public static async Task<int> Main(string[] args)
        {
            // FIRST statement, as in every sibling runner: point the default data directory at a per-run
            // temp path, so no runtime test resolves settings, repos, docks, or the code index under the
            // live Armada home.
            TestDataDirectory.Redirect();
            TestDataDirectory.Verify();

            // Adapter tests assert on the environment a captain process inherits, so the developer's provider
            // variables must not be present unless a test sets them.
            global::Test.Shared.Infrastructure.TestProcessEnvironment.RemoveProviderVariablesAndReport();
            // Test repositories are short-lived, so git auto maintenance after commits is start-up cost only.
            global::Test.Shared.Infrastructure.TestGitEnvironment.DisableAutoMaintenance();

            List<string> suiteFilters;
            try
            {
                suiteFilters = SuiteCommandLineOptions.Parse(args);
            }
            catch (ArgumentException ex)
            {
                Console.Error.WriteLine(ex.Message);
                return 1;
            }

            TestRunner runner = new TestRunner("ARMADA RUNTIME TEST SUITE");

            runner.AddSuite(new BaseAgentRuntimeTests());
            runner.AddSuite(new ClaudeCodeRuntimeTests());
            runner.AddSuite(new CodexRuntimeTests());
            runner.AddSuite(new GeminiRuntimeTests());
            runner.AddSuite(new CursorRuntimeTests());
            runner.AddSuite(new OpenCodeRuntimeTests());
            runner.AddSuite(new MuxRuntimeTests());
            runner.AddSuite(new ApiAgentRuntimeToolFailureTests());
            runner.AddSuite(new RunCommandToolTests());
            runner.AddSuite(new ApiAgentRuntimeCompactionTests());
            runner.AddSuite(new ApiAgentRuntimeCacheTests());

            runner.VerifyRegistration(typeof(Program).Assembly);

            int exitCode = await runner.RunAllAsync(suiteFilters).ConfigureAwait(false);
            return exitCode;
        }
    }
}
