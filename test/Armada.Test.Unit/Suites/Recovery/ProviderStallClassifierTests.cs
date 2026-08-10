namespace Armada.Test.Unit.Suites.Recovery
{
    using System;
    using System.Threading.Tasks;
    using Armada.Core.Services;
    using Armada.Test.Common;

    /// <summary>
    /// Unit tests for the pure provider-stall classifier and the in-memory progress tracker
    /// (harden OpenCode captains against silent provider stalls on long tool loops).
    /// </summary>
    public class ProviderStallClassifierTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Provider Stall Classifier";

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("FreshHeartbeatAndFreshProvider_ReturnsNone", async () =>
            {
                DateTime now = DateTime.UtcNow;
                ProviderStallKind kind = ProviderStallClassifier.Classify(
                    lastHeartbeatUtc: now.AddMinutes(-1),
                    lastProviderProgressUtc: now.AddSeconds(-30),
                    thresholdMinutes: 10,
                    nowUtc: now);

                AssertEqual(ProviderStallKind.None, kind, "a captain with recent heartbeat and recent provider progress must not be stalled");
                await Task.CompletedTask;
            });

            await RunTest("FreshHeartbeatStaleProvider_ReturnsProviderSilentStall", async () =>
            {
                DateTime now = DateTime.UtcNow;
                ProviderStallKind kind = ProviderStallClassifier.Classify(
                    lastHeartbeatUtc: now.AddMinutes(-1),
                    lastProviderProgressUtc: now.AddMinutes(-15),
                    thresholdMinutes: 10,
                    nowUtc: now);

                AssertEqual(ProviderStallKind.ProviderSilentStall, kind,
                    "a captain whose heartbeat is fresh but whose provider progress is stale must classify as a provider-silent stall");
                await Task.CompletedTask;
            });

            await RunTest("StaleHeartbeatAndStaleProvider_ReturnsHeartbeatAndProviderStall", async () =>
            {
                DateTime now = DateTime.UtcNow;
                ProviderStallKind kind = ProviderStallClassifier.Classify(
                    lastHeartbeatUtc: now.AddMinutes(-20),
                    lastProviderProgressUtc: now.AddMinutes(-20),
                    thresholdMinutes: 10,
                    nowUtc: now);

                AssertEqual(ProviderStallKind.HeartbeatAndProviderStall, kind,
                    "stale heartbeat with stale provider progress must classify as a combined stall");
                await Task.CompletedTask;
            });

            await RunTest("StaleHeartbeatOnly_ReturnsHeartbeatStall", async () =>
            {
                DateTime now = DateTime.UtcNow;
                ProviderStallKind kind = ProviderStallClassifier.Classify(
                    lastHeartbeatUtc: now.AddMinutes(-20),
                    lastProviderProgressUtc: now.AddMinutes(-1),
                    thresholdMinutes: 10,
                    nowUtc: now);

                AssertEqual(ProviderStallKind.HeartbeatStall, kind,
                    "stale heartbeat with fresh provider progress must classify as a heartbeat-only stall");
                await Task.CompletedTask;
            });

            await RunTest("LongHealthyToolLoopIsNeverStalled", async () =>
            {
                // A captain that has completed many steps recently must never be stalled, even if
                // the step count is far above the historical Mux ~50-iteration ceiling. The
                // classifier keys on recency, not on an absolute step count.
                DateTime now = DateTime.UtcNow;
                for (int step = 0; step < 100; step++)
                {
                    ProviderStallKind kind = ProviderStallClassifier.Classify(
                        lastHeartbeatUtc: now.AddSeconds(-5),
                        lastProviderProgressUtc: now.AddSeconds(-5),
                        thresholdMinutes: 10,
                        nowUtc: now);

                    AssertEqual(ProviderStallKind.None, kind, "step " + step + " must not be classified as stalled");
                }

                await Task.CompletedTask;
            });

            await RunTest("NeverRecordedProviderProgress_IsNotClassifiedAsProviderSilentStall", async () =>
            {
                DateTime now = DateTime.UtcNow;
                ProviderStallKind kind = ProviderStallClassifier.Classify(
                    lastHeartbeatUtc: now.AddMinutes(-1),
                    lastProviderProgressUtc: null,
                    thresholdMinutes: 10,
                    nowUtc: now);

                AssertEqual(ProviderStallKind.None, kind,
                    "a captain that has never reported provider progress cannot be classified as a provider-silent stall yet");
                await Task.CompletedTask;
            });

            await RunTest("ZeroOrNegativeThreshold_ClampsToPositive", async () =>
            {
                DateTime now = DateTime.UtcNow;
                // With the threshold clamped to 1.0 minute, a heartbeat 1 minute old and provider
                // progress 5 minutes old are both stale, so the classification is the combined
                // stall. The point of this test is that a non-positive threshold is clamped to a
                // positive value and the function stays total instead of throwing or mis-firing.
                ProviderStallKind kind = ProviderStallClassifier.Classify(
                    lastHeartbeatUtc: now.AddMinutes(-1),
                    lastProviderProgressUtc: now.AddMinutes(-5),
                    thresholdMinutes: 0,
                    nowUtc: now);

                AssertEqual(ProviderStallKind.HeartbeatAndProviderStall, kind,
                    "a non-positive threshold must be clamped to a positive value so the function stays total and classifies deterministically");
                await Task.CompletedTask;
            });

            await RunTest("TrackerRecordGetClearRoundTrips", async () =>
            {
                ProviderProgressTracker tracker = new ProviderProgressTracker();
                string captainId = "cpt_stall_test";
                DateTime stamped = DateTime.UtcNow;

                AssertFalse(tracker.TryGet(captainId, out _), "unrecorded captain must not be found");

                tracker.Record(captainId, stamped);
                AssertTrue(tracker.TryGet(captainId, out DateTime? last), "recorded captain must be found");
                AssertTrue(last.HasValue, "last progress timestamp must be present");
                AssertTrue(Math.Abs((last!.Value - stamped.ToUniversalTime()).TotalSeconds) < 1.0,
                    "recorded timestamp must be retained");

                tracker.Clear(captainId);
                AssertFalse(tracker.TryGet(captainId, out _), "cleared captain must not be found");
                await Task.CompletedTask;
            });

            await RunTest("TrackerIgnoresBlankCaptainId", async () =>
            {
                ProviderProgressTracker tracker = new ProviderProgressTracker();
                tracker.Record("", DateTime.UtcNow);
                AssertFalse(tracker.TryGet("", out _), "blank captain id must never be recorded");
                await Task.CompletedTask;
            });

            await RunTest("StartupGraceSkipsRecentlyStartedMission", () =>
            {
                DateTime now = DateTime.UtcNow;
                AssertTrue(ProviderStallClassifier.IsWithinStartupGrace(now.AddSeconds(-30), now, 1.0),
                    "30 seconds after start must be inside a 1-minute grace");
                AssertFalse(ProviderStallClassifier.IsWithinStartupGrace(now.AddMinutes(-5), now, 1.0),
                    "5 minutes after start must be outside the grace");
                AssertFalse(ProviderStallClassifier.IsWithinStartupGrace(null, now, 1.0),
                    "a mission with no recorded start is never in grace");
            });
        }
    }
}
