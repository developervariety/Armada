namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Server;
    using Armada.Test.Common;

    /// <summary>
    /// Tests for the admiral health-loop maintenance runner: each due step runs in isolation, so a
    /// step that fails on every run of a short cadence cannot starve a longer cadence whose cycle
    /// numbers are multiples of it.
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
        }
    }
}
