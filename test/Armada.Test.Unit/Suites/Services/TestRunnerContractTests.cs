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
            await RunTest("Shard options parse index and count and reject malformed values", () =>
            {
                TestHostOptions options = SuiteCommandLineOptions.ParseHost(new[] { "--shard", "2/6" });
                AssertEqual(2, options.Shard!.Index);
                AssertEqual(6, options.Shard!.Count);
                AssertTrue(SuiteCommandLineOptions.ParseHost(new[] { "--list-suites", "--shard", "1/1" }).ListSuites);
                foreach (string[] args in new[]
                {
                    new[] { "--shard" }, new[] { "--shard", "0/3" }, new[] { "--shard", "4/3" }, new[] { "--shard", "1/0" },
                    new[] { "--shard", "1" }, new[] { "--shard", "a/b" }, new[] { "--shard", "-1/3" }, new[] { "--shard", "1/2/3" },
                    new[] { "--shard", "1/2", "--shard", "2/2" }, new[] { "--shard", "1/2", "--suite", "alpha" }
                })
                {
                    AssertThrows<ArgumentException>(() => SuiteCommandLineOptions.ParseHost(args), String.Join(" ", args));
                }

                // A host that parses only suite filters must refuse shard options rather than ignore them.
                AssertThrows<ArgumentException>(() => SuiteCommandLineOptions.Parse(new[] { "--shard", "1/2" }));
                AssertThrows<ArgumentException>(() => SuiteCommandLineOptions.Parse(new[] { "--list-suites" }));
            });
            await RunTest("Shard assignment places every suite on exactly one shard, deterministically and balanced", () =>
            {
                List<string> names = new List<string>();
                ShardWeightsFile weights = new ShardWeightsFile { DefaultSeconds = 2.0 };
                for (int i = 0; i < 40; i++)
                {
                    names.Add("suite-" + i);
                    if (i % 3 != 0) weights.Suites["suite-" + i] = (i * 7) % 11;
                }

                SuiteShardPlan plan = new SuiteShardPlan(weights, new SerialSuitesFile());
                foreach (int count in new[] { 1, 2, 3, 6, 40, 45 })
                {
                    List<List<string>> shards = plan.Assign(names, count);
                    AssertEqual(count, shards.Count, "shard count " + count);
                    List<string> union = shards.SelectMany(shard => shard).ToList();
                    AssertEqual(names.Count, union.Count, "no suite duplicated across " + count + " shards");
                    AssertTrue(new HashSet<string>(union).SetEquals(names), "every suite assigned across " + count + " shards");
                    foreach (List<string> shard in shards)
                        AssertTrue(shard.SequenceEqual(names.Where(shard.Contains)), "a shard keeps registration order");

                    List<List<string>> again = plan.Assign(names, count);
                    for (int i = 0; i < count; i++)
                        AssertTrue(shards[i].SequenceEqual(again[i]), "assignment is deterministic for shard " + (i + 1) + " of " + count);

                    List<double> loads = shards.Select(shard => shard.Sum(plan.WeightOf)).ToList();
                    double heaviest = names.Max(plan.WeightOf);
                    AssertTrue(loads.Max() - loads.Min() <= heaviest, "shard loads differ by at most one suite across " + count + " shards");
                }
            });
            await RunTest("Serial suites run together on the first shard and an unregistered serial name fails", () =>
            {
                List<string> names = new List<string> { "heavy-a", "heavy-b", "global-one", "light", "global-two" };
                ShardWeightsFile weights = new ShardWeightsFile();
                weights.Suites["heavy-a"] = 50;
                weights.Suites["heavy-b"] = 40;
                SerialSuitesFile serial = new SerialSuitesFile();
                serial.Suites["global-one"] = "sets a process environment variable";
                serial.Suites["global-two"] = "binds a fixed port";
                SuiteShardPlan plan = new SuiteShardPlan(weights, serial);

                List<List<string>> shards = plan.Assign(names, 3);
                AssertTrue(shards[0].Contains("global-one") && shards[0].Contains("global-two"), "serial suites share shard 1");
                AssertFalse(shards[1].Contains("global-one") || shards[2].Contains("global-two"), "serial suites never leave shard 1");
                AssertEqual(names.Count, shards.Sum(shard => shard.Count), "serial suites are counted once");

                serial.Suites["renamed-away"] = "no longer registered";
                AssertThrows<InvalidOperationException>(() => new SuiteShardPlan(weights, serial).Assign(names, 3), "stale serial entry");
                AssertThrows<InvalidOperationException>(() => plan.Assign(new List<string> { "light", "light" }, 2), "duplicate suite names");
                SerialSuitesFile reasonless = new SerialSuitesFile();
                reasonless.Suites["light"] = " ";
                AssertThrows<InvalidOperationException>(() => new SuiteShardPlan(weights, reasonless), "serial entry without a reason");
            });
            await RunTest("A sharded run executes only its suites and an empty shard fails", async () =>
            {
                ShardWeightsFile weights = new ShardWeightsFile();
                weights.Suites["alpha"] = 3;
                weights.Suites["beta"] = 2;
                weights.Suites["gamma"] = 1;
                SuiteShardPlan plan = new SuiteShardPlan(weights, new SerialSuitesFile());
                AlphaSuite alpha = new AlphaSuite();
                BetaSuite beta = new BetaSuite();
                GammaSuite gamma = new GammaSuite();
                TestRunner runner = new TestRunner("shards");
                runner.AddSuite(alpha);
                runner.AddSuite(beta);
                runner.AddSuite(gamma);

                AssertEqual(0, await runner.RunAllAsync(null, new TestShard(1, 2), plan));
                AssertEqual(0, await runner.RunAllAsync(null, new TestShard(2, 2), plan));
                AssertEqual(1, alpha.Invocations, "alpha runs on exactly one shard");
                AssertEqual(1, beta.Invocations, "beta runs on exactly one shard");
                AssertEqual(1, gamma.Invocations, "gamma runs on exactly one shard");

                AssertEqual(1, await runner.RunAllAsync(null, new TestShard(4, 4), plan), "a shard assigned no suites fails");
                AssertEqual(1, await runner.RunAllAsync(null, new TestShard(1, 2), null), "a shard without a plan fails");
                AssertEqual(1, await runner.RunAllAsync(new[] { "alpha" }, new TestShard(1, 2), plan), "a shard with filters fails");
                AssertEqual(3, alpha.Invocations + beta.Invocations + gamma.Invocations, "refused runs execute nothing");
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

        private abstract class CountingSuite : TestSuite
        {
            public int Invocations { get; private set; }

            protected override Task RunTestsAsync()
            {
                Invocations++;
                return RunTest(Name + " case", () => { });
            }
        }

        private sealed class AlphaSuite : CountingSuite
        {
            public override string Name => "alpha";
        }

        private sealed class BetaSuite : CountingSuite
        {
            public override string Name => "beta";
        }

        private sealed class GammaSuite : CountingSuite
        {
            public override string Name => "gamma";
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
