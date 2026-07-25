namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Threading.Tasks;
    using Armada.Server;
    using Armada.Test.Common;

    /// <summary>
    /// Guards the reflection auto-fire cadence. Reflections were previously reachable only from
    /// the manual MCP audit-drain tool, so they fired three times across a thousand missions while
    /// every mission still consumed the learned-facts playbooks they produce. The sweeper gives the
    /// dispatcher a heartbeat; these tests pin the tick gate and the burst guard that keep that
    /// heartbeat from turning into a fan-out of one reflection voyage per vessel.
    /// </summary>
    public class ReflectionSweeperTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Reflection Sweeper";

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("FirstTick_AlwaysRuns", () =>
            {
                AssertTrue(ReflectionSweeper.ShouldRunTick(null, DateTime.UtcNow, 30),
                    "a sweeper that has never ticked must run so an overdue fleet is picked up at startup");
            });

            await RunTest("TickWithinInterval_IsSkipped", () =>
            {
                DateTime now = DateTime.UtcNow;
                AssertTrue(!ReflectionSweeper.ShouldRunTick(now.AddMinutes(-1), now, 30),
                    "a tick one minute into a 30-minute interval must not run");
                AssertTrue(!ReflectionSweeper.ShouldRunTick(now.AddMinutes(-29.5), now, 30),
                    "a tick just under the interval must not run");
            });

            await RunTest("TickAtOrBeyondInterval_Runs", () =>
            {
                DateTime now = DateTime.UtcNow;
                AssertTrue(ReflectionSweeper.ShouldRunTick(now.AddMinutes(-30), now, 30),
                    "a tick exactly at the interval must run");
                AssertTrue(ReflectionSweeper.ShouldRunTick(now.AddHours(-6), now, 30),
                    "a long-overdue tick must run");
            });

            await RunTest("HeartbeatCadence_DoesNotOverTrigger", () =>
            {
                // The health loop calls the sweeper every heartbeat (seconds). Only the interval
                // gate stops that from evaluating -- and dispatching -- on every beat.
                DateTime now = DateTime.UtcNow;
                DateTime lastTick = now.AddSeconds(-5);
                AssertTrue(!ReflectionSweeper.ShouldRunTick(lastTick, now, 30),
                    "a 5-second heartbeat must not trigger a reflection sweep");
            });
        }
    }
}
