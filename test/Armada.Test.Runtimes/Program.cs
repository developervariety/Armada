namespace Armada.Test.Runtimes
{
    using Armada.Test.Common;
    using Armada.Test.Runtimes.Suites;

    public class Program
    {
        public static async Task<int> Main(string[] args)
        {
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

            runner.AddSuite(new AgentRuntimeFactoryTests());
            runner.AddSuite(new BaseAgentRuntimeTests());
            runner.AddSuite(new ClaudeCodeRuntimeTests());
            runner.AddSuite(new CodexRuntimeTests());
            runner.AddSuite(new GeminiRuntimeTests());
            runner.AddSuite(new CursorRuntimeTests());
            runner.AddSuite(new OpenCodeRuntimeTests());
            runner.AddSuite(new MuxRuntimeTests());

            runner.VerifyRegistration(typeof(Program).Assembly);

            int exitCode = await runner.RunAllAsync(suiteFilters).ConfigureAwait(false);
            return exitCode;
        }
    }
}
