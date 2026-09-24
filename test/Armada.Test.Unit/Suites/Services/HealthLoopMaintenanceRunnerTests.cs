namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Services;
    using Armada.Server;
    using Armada.Test.Common;

    /// <summary>
    /// Tests for the admiral health-loop maintenance runner: each due step runs in isolation, so a
    /// step that fails on every run of a short cadence cannot starve a longer cadence whose cycle
    /// numbers are multiples of it. Also covers the model endpoint health loop, which runs beside the
    /// core heartbeat loop so a blocked provider probe cannot hold the heartbeat.
    /// </summary>
    public class HealthLoopMaintenanceRunnerTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Health Loop Maintenance Runner";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("A step failing on a shorter cadence does not skip a longer cadence on the same cycle", async () =>
            {
                List<string> ran = new List<string>();
                List<string> failed = new List<string>();
                List<HealthLoopMaintenanceStep> steps = new List<HealthLoopMaintenanceStep>
                {
                    HealthLoopMaintenanceStep.EveryCycles("data expiry", () => 100, t => throw new InvalidOperationException("unsupported connection string")),
                    HealthLoopMaintenanceStep.EveryCycles("disk lifecycle reconciliation", () => 50, t => { ran.Add("disk"); return Task.CompletedTask; }),
                    HealthLoopMaintenanceStep.EveryCycles("branch cleanup sweep", () => 200, t => { ran.Add("branch"); return Task.CompletedTask; })
                };

                int failures = 0;
                for (int cycle = 1; cycle <= 400; cycle++)
                {
                    failures += await HealthLoopMaintenanceRunner.RunDueStepsAsync(
                        cycle, steps, (name, ex) => failed.Add(name), CancellationToken.None).ConfigureAwait(false);
                }

                AssertEqual(2, ran.Count(r => r == "branch"), "the branch sweep must run on cycles 200 and 400 although data expiry throws on both");
                AssertEqual(8, ran.Count(r => r == "disk"), "disk reconciliation must run on every multiple of 50");
                AssertEqual(4, failures, "each data expiry failure must be counted");
                AssertTrue(failed.Count == 4 && failed.All(n => n == "data expiry"), "only the failing step may be reported, by name");
            }).ConfigureAwait(false);

            await RunTest("A health check that throws on every cycle still lets every sweep trigger run", async () =>
            {
                List<string> triggered = new List<string>();
                List<string> failed = new List<string>();
                List<HealthLoopMaintenanceStep> cycleSteps = ArmadaServer.BuildHealthCycleSteps(
                    t => throw new InvalidOperationException("poison mission row"),
                    t => triggered.Add("check runs"),
                    t => triggered.Add("recovery"),
                    t => triggered.Add("incidents"),
                    t => triggered.Add("scheduler"));

                int failures = 0;
                for (int cycle = 1; cycle <= 3; cycle++)
                {
                    failures += await HealthLoopMaintenanceRunner.RunDueStepsAsync(
                        cycle, cycleSteps, (name, ex) => failed.Add(name), CancellationToken.None).ConfigureAwait(false);
                }

                AssertEqual(3, failures, "the health check failure must be counted on each cycle");
                AssertTrue(failed.All(n => n == "health check"), "only the failing health check may be reported, by name");
                foreach (string sweep in new[] { "check runs", "recovery", "incidents", "scheduler" })
                    AssertEqual(3, triggered.Count(t => t == sweep), "the " + sweep + " sweep must be triggered on every cycle");
            }).ConfigureAwait(false);

            await RunTest("A step that is not due does not run", async () =>
            {
                int longRuns = 0;
                int everyRuns = 0;
                List<HealthLoopMaintenanceStep> steps = new List<HealthLoopMaintenanceStep>
                {
                    HealthLoopMaintenanceStep.EveryCycles("long", () => 200, t => { longRuns++; return Task.CompletedTask; }),
                    HealthLoopMaintenanceStep.EveryCycles("every", () => 1, t => { everyRuns++; return Task.CompletedTask; })
                };

                int failures = await HealthLoopMaintenanceRunner.RunDueStepsAsync(
                    150, steps, (name, ex) => { }, CancellationToken.None).ConfigureAwait(false);

                AssertEqual(0, failures, "no step failed");
                AssertEqual(0, longRuns, "a 200-cycle step must not run on cycle 150");
                AssertEqual(1, everyRuns, "a one-cycle step runs on every cycle");
            }).ConfigureAwait(false);

            await RunTest("The interval is read on each cycle so a settings change applies without a restart", async () =>
            {
                int interval = 200;
                int runs = 0;
                List<HealthLoopMaintenanceStep> steps = new List<HealthLoopMaintenanceStep>
                {
                    HealthLoopMaintenanceStep.EveryCycles("sweep", () => interval, t => { runs++; return Task.CompletedTask; })
                };

                await HealthLoopMaintenanceRunner.RunDueStepsAsync(100, steps, (name, ex) => { }, CancellationToken.None).ConfigureAwait(false);
                AssertEqual(0, runs, "cycle 100 is not due at a 200-cycle interval");

                interval = 100;
                await HealthLoopMaintenanceRunner.RunDueStepsAsync(100, steps, (name, ex) => { }, CancellationToken.None).ConfigureAwait(false);
                AssertEqual(1, runs, "cycle 100 is due once the interval changes to 100");
            }).ConfigureAwait(false);

            await RunTest("A step whose due check throws is reported and later steps still run", async () =>
            {
                List<string> failed = new List<string>();
                int laterRuns = 0;
                List<HealthLoopMaintenanceStep> steps = new List<HealthLoopMaintenanceStep>
                {
                    new HealthLoopMaintenanceStep("broken predicate", cycle => throw new InvalidOperationException("bad interval"), t => Task.CompletedTask),
                    HealthLoopMaintenanceStep.EveryCycles("later", () => 1, t => { laterRuns++; return Task.CompletedTask; })
                };

                int failures = await HealthLoopMaintenanceRunner.RunDueStepsAsync(
                    1, steps, (name, ex) => failed.Add(name), CancellationToken.None).ConfigureAwait(false);

                AssertEqual(1, failures, "the predicate failure must be counted");
                AssertTrue(failed.SequenceEqual(new[] { "broken predicate" }), "the failing step must be named");
                AssertEqual(1, laterRuns, "the later step must still run");
            }).ConfigureAwait(false);

            await RunTest("Cancellation stops the run instead of reading as a step failure", async () =>
            {
                List<string> failed = new List<string>();
                using (CancellationTokenSource cts = new CancellationTokenSource())
                {
                    List<HealthLoopMaintenanceStep> steps = new List<HealthLoopMaintenanceStep>
                    {
                        HealthLoopMaintenanceStep.EveryCycles("cancelling", () => 1, t => { cts.Cancel(); t.ThrowIfCancellationRequested(); return Task.CompletedTask; }),
                        HealthLoopMaintenanceStep.EveryCycles("after", () => 1, t => { failed.Add("after ran"); return Task.CompletedTask; })
                    };

                    bool cancelled = false;
                    try
                    {
                        await HealthLoopMaintenanceRunner.RunDueStepsAsync(1, steps, (name, ex) => failed.Add(name), cts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        cancelled = true;
                    }

                    AssertTrue(cancelled, "cancellation must propagate to the health loop");
                    AssertEqual(0, failed.Count, "cancellation is neither a step failure nor a reason to run later steps");
                }
            }).ConfigureAwait(false);

            await RunTest("ArmadaServer isolates model endpoint health from the core heartbeat loop", () =>
            {
                string path = Path.Combine(FindRepositoryRoot(), "src", "Armada.Server", "ArmadaServer.cs");
                string contents = File.ReadAllText(path);
                int coreStart = contents.IndexOf("private async Task HealthCheckLoopAsync", StringComparison.Ordinal);
                int endpointStart = contents.IndexOf("private async Task ModelEndpointHealthLoopAsync", StringComparison.Ordinal);
                AssertTrue(coreStart >= 0 && endpointStart > coreStart, "ArmadaServer should define both health loops");
                string coreLoop = contents.Substring(coreStart, endpointStart - coreStart);
                AssertFalse(coreLoop.Contains("CheckHealthAllAsync", StringComparison.Ordinal), "A blocked model provider must not block the core heartbeat loop");
                AssertContains("_ModelEndpointService.CheckHealthAllAsync(sweepToken)", contents, "The endpoint health loop must retain the provider sweep");
                AssertContains("_ModelEndpointHealthTask = ModelEndpointHealthLoopAsync(_TokenSource.Token)", contents, "The endpoint health loop must start independently");
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("The endpoint sweep runner reads the interval after every sweep so a reloaded heartbeat applies", async () =>
            {
                TimeSpan interval = TimeSpan.FromHours(1);
                int sweeps = 0;
                TaskCompletionSource<bool> reloadedThird = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                using (CancellationTokenSource cancellation = new CancellationTokenSource())
                {
                    Task loop = ModelEndpointHealthSweepRunner.RunAsync(
                        token =>
                        {
                            int count = Interlocked.Increment(ref sweeps);
                            // The reload lands during the first sweep, before the first wait is read.
                            if (count == 1) interval = TimeSpan.FromMilliseconds(10);
                            if (count == 3) reloadedThird.TrySetResult(true);
                            return Task.CompletedTask;
                        },
                        () => interval,
                        _ => { },
                        cancellation.Token);

                    bool reloaded = await Task.WhenAny(reloadedThird.Task, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false) == reloadedThird.Task;
                    cancellation.Cancel();
                    await loop.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                    AssertTrue(reloaded, "a heartbeat interval shortened by a reload must shorten the next wait without a restart");
                }
            }).ConfigureAwait(false);

            await RunTest("The endpoint sweep runner never overlaps a blocked probe and cancels it cleanly", async () =>
            {
                TaskCompletionSource<bool> sweepStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                using (CancellationTokenSource cancellation = new CancellationTokenSource())
                {
                    int activeSweeps = 0;
                    int maximumActiveSweeps = 0;
                    int sweepCount = 0;

                    Task sweep = ModelEndpointHealthSweepRunner.RunAsync(
                        async token =>
                        {
                            Interlocked.Increment(ref sweepCount);
                            int active = Interlocked.Increment(ref activeSweeps);
                            int observedMaximum;
                            do
                            {
                                observedMaximum = maximumActiveSweeps;
                                if (active <= observedMaximum) break;
                            }
                            while (Interlocked.CompareExchange(ref maximumActiveSweeps, active, observedMaximum) != observedMaximum);

                            sweepStarted.TrySetResult(true);
                            try
                            {
                                await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
                            }
                            finally
                            {
                                Interlocked.Decrement(ref activeSweeps);
                            }
                        },
                        () => TimeSpan.Zero,
                        _ => { },
                        cancellation.Token);

                    await sweepStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                    AssertFalse(sweep.IsCompleted, "the runner must stay inside the blocked sweep until cancelled");
                    cancellation.Cancel();
                    await sweep.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

                    AssertEqual(1, sweepCount, "A blocked sweep must not overlap or start a second probe.");
                    AssertEqual(1, maximumActiveSweeps, "Endpoint probes must run serially.");
                    AssertEqual(0, activeSweeps, "Cancellation must release the active probe.");
                }
            }).ConfigureAwait(false);
        }

        private static string FindRepositoryRoot()
        {
            DirectoryInfo? current = new DirectoryInfo(AppContext.BaseDirectory);
            while (current != null)
            {
                if (Directory.Exists(Path.Combine(current.FullName, "src"))
                    && Directory.Exists(Path.Combine(current.FullName, "test")))
                {
                    return current.FullName;
                }

                current = current.Parent;
            }

            throw new DirectoryNotFoundException("Could not locate repository root from test base directory.");
        }
    }
}
