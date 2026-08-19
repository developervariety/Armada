namespace Armada.Test.Unit
{
    using System;
    using System.IO;
    using Armada.Core.Models;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// Pins the separation between the two captain timestamps. Stall detection measures the age of
    /// LastHeartbeatUtc, which must advance only on real agent output. The process-liveness loop runs
    /// on a timer for any process that is merely alive, so if it advances that same field the stall
    /// threshold can never be exceeded and a silent-but-running agent is never detected. The loop
    /// therefore writes LastProcessAliveUtc instead, and these tests hold those roles apart.
    /// </summary>
    public sealed class CaptainProcessLivenessTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Captain Process Liveness";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("UpdateProcessAliveAsync does not advance the output heartbeat", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    Captain captain = new Captain("liveness-captain");
                    await testDb.Driver.Captains.CreateAsync(captain);

                    // Establish an output heartbeat, then age it past any plausible stall threshold.
                    await testDb.Driver.Captains.UpdateHeartbeatAsync(captain.Id);
                    Captain? afterOutput = await testDb.Driver.Captains.ReadAsync(captain.Id);
                    AssertNotNull(afterOutput, "Captain should exist after heartbeat");
                    Assert(afterOutput!.LastHeartbeatUtc.HasValue, "Output heartbeat should be set");
                    DateTime heartbeatBefore = afterOutput.LastHeartbeatUtc!.Value;

                    await Task.Delay(1100);

                    // The liveness loop's write. This must leave the output heartbeat alone.
                    await testDb.Driver.Captains.UpdateProcessAliveAsync(captain.Id);

                    Captain? afterAlive = await testDb.Driver.Captains.ReadAsync(captain.Id);
                    AssertNotNull(afterAlive, "Captain should exist after process-alive update");
                    Assert(
                        afterAlive!.LastProcessAliveUtc.HasValue,
                        "Process-alive timestamp should be set");
                    AssertEqual(
                        heartbeatBefore.ToString("O"),
                        afterAlive.LastHeartbeatUtc!.Value.ToString("O"),
                        "Output heartbeat must be unchanged by a process-alive update");
                    Assert(
                        afterAlive.LastProcessAliveUtc!.Value > heartbeatBefore,
                        "Process-alive timestamp should be newer than the aged output heartbeat");
                }
            });

            await RunTest("UpdateHeartbeatAsync still advances the output heartbeat", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    Captain captain = new Captain("heartbeat-captain");
                    await testDb.Driver.Captains.CreateAsync(captain);

                    await testDb.Driver.Captains.UpdateHeartbeatAsync(captain.Id);
                    Captain? first = await testDb.Driver.Captains.ReadAsync(captain.Id);
                    DateTime firstBeat = first!.LastHeartbeatUtc!.Value;

                    await Task.Delay(1100);
                    await testDb.Driver.Captains.UpdateHeartbeatAsync(captain.Id);

                    Captain? second = await testDb.Driver.Captains.ReadAsync(captain.Id);
                    Assert(
                        second!.LastHeartbeatUtc!.Value > firstBeat,
                        "Real agent output must still advance the output heartbeat");
                }
            });

            await RunTest("Process-liveness column round-trips through the captain mapper", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    Captain captain = new Captain("liveness-roundtrip-captain");
                    await testDb.Driver.Captains.CreateAsync(captain);

                    Captain? fresh = await testDb.Driver.Captains.ReadAsync(captain.Id);
                    AssertNotNull(fresh, "Captain should exist");
                    Assert(
                        !fresh!.LastProcessAliveUtc.HasValue,
                        "A captain that has never been observed alive carries no process-alive time");

                    await testDb.Driver.Captains.UpdateProcessAliveAsync(captain.Id);

                    Captain? observed = await testDb.Driver.Captains.ReadAsync(captain.Id);
                    Assert(
                        observed!.LastProcessAliveUtc.HasValue,
                        "Process-alive time should survive the round-trip through the mapper");
                }
            });

            // The masking bug lived in the liveness loop's choice of call, not in the database, so
            // the loop itself is pinned with a source guard: it is a private fire-and-forget task
            // with no seam to observe, and the call it makes is the whole of the fix.

            await RunTest("Liveness loop writes process-alive, never the output heartbeat", () =>
            {
                string path = Path.Combine(
                    FindRepositoryRoot(), "src", "Armada.Server", "AgentLifecycleHandler.cs");
                string contents = File.ReadAllText(path);

                int loopStart = contents.IndexOf("private void StartProcessLivenessHeartbeat", StringComparison.Ordinal);
                Assert(loopStart >= 0, "StartProcessLivenessHeartbeat should exist");

                int loopEnd = contents.IndexOf("private static bool IsTrackedProcessAlive", StringComparison.Ordinal);
                Assert(loopEnd > loopStart, "Should locate the end of the liveness loop");

                string loop = contents.Substring(loopStart, loopEnd - loopStart);

                Assert(
                    loop.Contains("Captains.UpdateProcessAliveAsync"),
                    "The liveness loop must refresh process-liveness telemetry");
                Assert(
                    !loop.Contains("Captains.UpdateHeartbeatAsync"),
                    "The liveness loop must never advance the captain output heartbeat: doing so " +
                    "resets the value stall detection measures, so no captain is ever seen as stalled");
            });
        }

        /// <summary>
        /// Walk up from the test binary until the directory containing the solution's src folder is
        /// found, so the source guard does not depend on the working directory a runner chose.
        /// </summary>
        /// <returns>Absolute path to the repository root.</returns>
        private static string FindRepositoryRoot()
        {
            DirectoryInfo? dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, "src", "Armada.Server"))) return dir.FullName;
                dir = dir.Parent;
            }

            throw new DirectoryNotFoundException("Could not locate the repository root from " + AppContext.BaseDirectory);
        }
    }
}
