namespace Armada.Test.Unit.Suites.Services
{
    using Armada.Test.Common;

    /// <summary>
    /// Failure sentinels for the executable test runner and assertion helpers.
    /// </summary>
    public class TestRunnerContractTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Test runner contracts";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("Empty registry fails", async () =>
            {
                AssertEqual(1, await new TestRunner("empty").RunAllAsync());
            });
            await RunTest("Empty suite fails", async () =>
            {
                TestRunner runner = new TestRunner("empty suite");
                runner.AddSuite(new ProbeSuite());
                AssertEqual(1, await runner.RunAllAsync());
            });
            await RunTest("Unknown and blank filters fail before execution", async () =>
            {
                foreach (string[] filters in new[] { new[] { "missing" }, new[] { "probe", "missing" }, new[] { " " } })
                {
                    ProbeSuite suite = new ProbeSuite { Body = probe => probe.PassAsync() };
                    TestRunner runner = new TestRunner("filter");
                    runner.AddSuite(suite);
                    AssertEqual(1, await runner.RunAllAsync(filters));
                    AssertEqual(0, suite.Invocations);
                }
            });
            await RunTest("Valid filter executes once", async () =>
            {
                ProbeSuite suite = new ProbeSuite { Body = probe => probe.PassAsync() };
                TestRunner runner = new TestRunner("filter");
                runner.AddSuite(suite);
                AssertEqual(0, await runner.RunAllAsync(new[] { "probe" }));
                AssertEqual(1, suite.Invocations);
            });
            await RunTest("Throwing case fails and runs once", async () =>
            {
                ProbeSuite suite = new ProbeSuite { Body = probe => probe.FailAsync() };
                TestRunner runner = new TestRunner("failure");
                runner.AddSuite(suite);
                AssertEqual(1, await runner.RunAllAsync());
                AssertEqual(1, suite.Invocations);
            });
            await RunTest("Runner reuse clears earlier failures", async () =>
            {
                ProbeSuite suite = new ProbeSuite { Body = probe => probe.FailAsync() };
                TestRunner runner = new TestRunner("reuse");
                runner.AddSuite(suite);
                AssertEqual(1, await runner.RunAllAsync());
                suite.Body = probe => probe.PassAsync();
                AssertEqual(0, await runner.RunAllAsync());
                AssertEqual(2, suite.Invocations);
            });
            await RunTest("Duplicate suite type is rejected", () =>
            {
                TestRunner runner = new TestRunner("duplicate");
                runner.AddSuite(new ProbeSuite());
                AssertThrows<ArgumentException>(() => runner.AddSuite(new ProbeSuite()));
            });
            await RunTest("Setup failure is a failed infrastructure result", async () =>
            {
                ProbeSuite suite = new ProbeSuite { Body = probe => throw new InvalidOperationException("setup") };
                List<TestResult> results = await suite.RunAsync();
                AssertEqual(1, results.Count);
                AssertFalse(results[0].Passed);
                AssertContains("setup", results[0].Message!);
            });
            await RunTest("Failure after a case retains completed results", async () =>
            {
                ProbeSuite suite = new ProbeSuite { Body = async probe =>
                {
                    await probe.PassAsync();
                    throw new InvalidOperationException("teardown");
                }};
                List<TestResult> results = await suite.RunAsync();
                AssertEqual(2, results.Count);
                AssertTrue(results[0].Passed);
                AssertFalse(results[1].Passed);
                AssertContains("teardown", results[1].Message!);
            });
            await RunTest("Cancellation outside a case fails", async () =>
            {
                ProbeSuite suite = new ProbeSuite { Body = probe => throw new OperationCanceledException() };
                List<TestResult> results = await suite.RunAsync();
                AssertEqual(1, results.Count);
                AssertFalse(results[0].Passed);
            });
            await RunTest("Broad synchronous exception assertion rejects no throw", () =>
            {
                bool failed = false;
                try { AssertThrows<Exception>(() => { }); }
                catch (Exception) { failed = true; }
                AssertTrue(failed, "No-throw action must fail the assertion");
            });
            await RunTest("Broad asynchronous exception assertion rejects no throw", async () =>
            {
                bool failed = false;
                try { await AssertThrowsAsync<Exception>(() => Task.CompletedTask); }
                catch (Exception) { failed = true; }
                AssertTrue(failed, "No-throw action must fail the assertion");
            });
            await RunTest("Missing compiled suite registration fails", () =>
            {
                TestRunner runner = new TestRunner("registry");
                AssertThrows<InvalidOperationException>(() => runner.VerifyRegistration(new[] { typeof(ProbeSuite) }));
                runner.AddSuite(new ProbeSuite());
                runner.VerifyRegistration(new[] { typeof(ProbeSuite) });
            });
            await RunTest("Duplicate case does not execute twice", async () =>
            {
                ProbeSuite suite = new ProbeSuite { Body = async probe => { await probe.PassAsync(); await probe.PassAsync(); } };
                List<TestResult> results = await suite.RunAsync();
                AssertEqual(1, suite.Invocations);
                AssertEqual(2, results.Count);
                AssertFalse(results[1].Passed);
            });
            await RunTest("Malformed suite arguments fail", () =>
            {
                foreach (string[] args in new[] { new[] { "--suite" }, new[] { "--suite", " " }, new[] { "--suite", "--no-cleanup" }, new[] { "--typo" } })
                    AssertThrows<ArgumentException>(() => SuiteCommandLineOptions.Parse(args));
                AssertEqual(2, SuiteCommandLineOptions.Parse(new[] { "--suite", "first", "--suite", "second" }).Count);
                AssertThrows<ArgumentException>(() => CommandLineOptions.Parse(new[] { "--suite" }));
                AssertEqual("first", CommandLineOptions.Parse(new[] { "--suite", "first" }).SuiteFilters.Single());
            });
            await RunTest("Named skips retain reasons and cannot pass an empty run", async () =>
            {
                ProbeSuite suite = new ProbeSuite { Body = probe => { probe.Skip(); return Task.CompletedTask; } };
                TestRunner runner = new TestRunner("skips");
                runner.AddSuite(suite);
                AssertEqual(1, await runner.RunAllAsync());
                List<TestResult> results = await suite.RunAsync();
                AssertEqual("fixture capability is absent", results.Single().SkipReason);
                suite.Body = async probe => { probe.Skip(); await probe.PassAsync(); };
                AssertEqual(0, await runner.RunAllAsync());
            });
            await RunTest("Unused database stub cannot pass zero cases", async () =>
            {
                SyslogLogging.LoggingModule logging = new SyslogLogging.LoggingModule();
                logging.Settings.EnableConsole = false;
                using (Armada.Core.Database.Sqlite.SqliteDatabaseDriver driver = new Armada.Core.Database.Sqlite.SqliteDatabaseDriver("Data Source=:memory:", logging))
                {
                    Armada.Test.Common.DatabaseTestRunner runner = new Armada.Test.Common.DatabaseTestRunner(driver, false);
                    AssertEqual(1, await runner.RunAllTestsAsync());
                }
            });
            await RunTest("Expected exception assertions still pass", async () =>
            {
                AssertThrows<Exception>(() => throw new InvalidOperationException());
                await AssertThrowsAsync<Exception>(() => Task.FromException(new InvalidOperationException()));
            });
        }

        private sealed class ProbeSuite : TestSuite
        {
            public override string Name => "probe";
            public Func<ProbeSuite, Task> Body { get; set; } = probe => Task.CompletedTask;
            public int Invocations { get; private set; }
            protected override Task RunTestsAsync() => Body(this);
            public void Skip() => SkipTest("named skip sentinel", "fixture capability is absent");
            public Task PassAsync() => RunTest("passing sentinel", () => { Invocations++; });
            public Task FailAsync() => RunTest("failing sentinel", () =>
            {
                Invocations++;
                throw new InvalidOperationException("sentinel");
            });
        }
    }
}
