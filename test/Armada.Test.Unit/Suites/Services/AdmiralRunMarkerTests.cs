namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.IO;
    using System.Threading.Tasks;
    using Armada.Server;
    using Armada.Test.Common;

    /// <summary>
    /// Tests for the admiral run marker: a run that stops cleanly reports nothing on the next start,
    /// and a run that was killed is reported with the last time it was alive and its memory then.
    /// </summary>
    public class AdmiralRunMarkerTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Admiral Run Marker";

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("A run that stopped cleanly is not reported on the next start", () =>
            {
                string dir = NewDirectory();
                try
                {
                    AdmiralRunRecord? previous;
                    AdmiralRunMarker first = AdmiralRunMarker.Start(dir, DateTime.UtcNow, 101, out previous);
                    AssertNull(previous, "a first start has nothing to report");
                    first.Beat(DateTime.UtcNow, 500L * 1024 * 1024, null, null);
                    first.MarkCleanExit(DateTime.UtcNow);

                    AdmiralRunMarker.Start(dir, DateTime.UtcNow, 102, out previous);
                    AssertNull(previous, "a clean stop is not reported");
                }
                finally
                {
                    Directory.Delete(dir, true);
                }
                return Task.CompletedTask;
            });

            await RunTest("A run that was killed is reported with its last alive time and memory", () =>
            {
                string dir = NewDirectory();
                try
                {
                    DateTime lastAlive = new DateTime(2026, 9, 25, 20, 11, 2, DateTimeKind.Utc);
                    AdmiralRunRecord? previous;
                    AdmiralRunMarker killed = AdmiralRunMarker.Start(dir, lastAlive.AddMinutes(-30), 201, out previous);
                    killed.Beat(lastAlive, 7800L * 1024 * 1024, 12200L * 1024 * 1024, 12288L * 1024 * 1024);
                    // No MarkCleanExit: the process was killed.

                    AdmiralRunMarker.Start(dir, lastAlive.AddMinutes(2), 202, out previous);
                    AssertNotNull(previous, "a run with no clean stop is reported");
                    AssertEqual(201, previous!.ProcessId);
                    AssertEqual(lastAlive, previous.LastAliveUtc.ToUniversalTime(), "the last alive time is kept");
                    string description = AdmiralRunMarker.Describe(previous);
                    AssertContains("7800 MiB", description, "the heap at the last beat is reported");
                    AssertContains("12200 MiB", description, "the container memory at the last beat is reported");
                    AssertContains("12288 MiB limit", description, "the container limit is reported");
                    AssertContains("journalctl -k", description, "the report says where the cause is recorded");

                    AdmiralRunMarker.Start(dir, lastAlive.AddMinutes(3), 203, out previous);
                    AssertNotNull(previous, "the second start after a kill reports the killed run 202, which never stopped either");
                    AssertEqual(202, previous!.ProcessId);
                }
                finally
                {
                    Directory.Delete(dir, true);
                }
                return Task.CompletedTask;
            });

            await RunTest("An unreadable marker is not reported and is replaced", () =>
            {
                string dir = NewDirectory();
                try
                {
                    File.WriteAllText(Path.Combine(dir, AdmiralRunMarker.FileName), "{ not json");
                    AdmiralRunRecord? previous;
                    AdmiralRunMarker marker = AdmiralRunMarker.Start(dir, DateTime.UtcNow, 301, out previous);
                    AssertNull(previous, "an unreadable marker yields no report");
                    AssertContains("\"ProcessId\":301", File.ReadAllText(marker.Path), "the new run's marker replaces it");
                }
                finally
                {
                    Directory.Delete(dir, true);
                }
                return Task.CompletedTask;
            });
        }

        private static string NewDirectory()
        {
            string dir = Path.Combine(Path.GetTempPath(), "armada-run-marker-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}
