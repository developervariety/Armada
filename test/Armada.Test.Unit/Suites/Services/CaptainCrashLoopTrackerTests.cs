namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using Armada.Test.Common;

    /// <summary>
    /// Behavioral tests for bounded crash-loop detection and duplicate exit handling.
    /// </summary>
    public sealed class CaptainCrashLoopTrackerTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Captain Crash Loop Tracker";

        /// <inheritdoc />
        protected override Task RunTestsAsync()
        {
            RunTest("Threshold counts distinct failures inside the window", () =>
            {
                CrashLoopDetectionSettings settings = new CrashLoopDetectionSettings { FailureThreshold = 3, WindowMinutes = 5 };
                CaptainCrashLoopTracker tracker = new CaptainCrashLoopTracker(settings);
                DateTime now = DateTime.UtcNow;

                AssertFalse(tracker.Record("cpt_test", "p1:cpt_test:m1", now, out int first), "First crash must stay below threshold.");
                AssertFalse(tracker.Record("cpt_test", "p2:cpt_test:m2", now.AddMinutes(1), out int second), "Second crash must stay below threshold.");
                AssertTrue(tracker.Record("cpt_test", "p3:cpt_test:m3", now.AddMinutes(2), out int third), "Third crash must reach threshold.");
                AssertEqual(3, third, "Threshold count must include three distinct failures.");
            });

            RunTest("A settings reload changes the threshold and window the running tracker counts against", () =>
            {
                ArmadaSettings live = new ArmadaSettings();
                live.CrashLoopDetection.FailureThreshold = 5;
                live.CrashLoopDetection.WindowMinutes = 60;
                CaptainCrashLoopTracker tracker = new CaptainCrashLoopTracker(live.CrashLoopDetection);

                ArmadaSettings edited = new ArmadaSettings();
                edited.CrashLoopDetection.FailureThreshold = 2;
                edited.CrashLoopDetection.WindowMinutes = 1;
                live.ApplyHotReloadableFrom(edited);

                DateTime now = DateTime.UtcNow;
                AssertFalse(tracker.Record("cpt_reload", "p1:cpt_reload:m1", now, out int _), "First crash stays below the reloaded threshold.");
                AssertTrue(tracker.Record("cpt_reload", "p2:cpt_reload:m2", now.AddSeconds(10), out int second),
                    "The second crash must reach the reloaded threshold of two without a restart.");
                AssertEqual(2, second, "Both crashes count inside the window.");
                AssertFalse(tracker.Record("cpt_reload", "p3:cpt_reload:m3", now.AddMinutes(5), out int later),
                    "A crash five minutes later must fall outside the reloaded one-minute window.");
                AssertEqual(1, later, "The reloaded window must expire the earlier crashes.");
            });

            RunTest("Duplicate process failure does not advance count", () =>
            {
                CrashLoopDetectionSettings settings = new CrashLoopDetectionSettings { FailureThreshold = 2 };
                CaptainCrashLoopTracker tracker = new CaptainCrashLoopTracker(settings);
                DateTime now = DateTime.UtcNow;

                AssertFalse(tracker.Record("cpt_test", "same-process", now, out int first), "First failure must stay below threshold.");
                AssertFalse(tracker.Record("cpt_test", "same-process", now, out int duplicate), "Duplicate failure must stay below threshold.");
                AssertEqual(first, duplicate, "Duplicate failure must not advance the count.");
                AssertTrue(tracker.Record("cpt_test", "next-process", now, out int second), "A distinct failure must reach threshold.");
                AssertEqual(2, second, "Only distinct failures must count.");
            });

            RunTest("Expired failures are removed and reset clears state", () =>
            {
                CrashLoopDetectionSettings settings = new CrashLoopDetectionSettings { FailureThreshold = 2, WindowMinutes = 1 };
                CaptainCrashLoopTracker tracker = new CaptainCrashLoopTracker(settings);
                DateTime now = DateTime.UtcNow;

                AssertFalse(tracker.Record("cpt_test", "old", now, out _), "One failure must stay below threshold.");
                AssertFalse(tracker.Record("cpt_test", "new", now.AddMinutes(2), out int afterExpiry), "Expired failure must not count.");
                AssertEqual(1, afterExpiry, "Only the recent failure must remain.");
                tracker.Reset("cpt_test");
                AssertFalse(tracker.Record("cpt_test", "after-reset", now.AddMinutes(2), out int afterReset), "Reset state must start below threshold.");
                AssertEqual(1, afterReset, "Reset must clear prior failures.");
            });

            RunTest("Captain history is bounded", () =>
            {
                CrashLoopDetectionSettings settings = new CrashLoopDetectionSettings { FailureThreshold = 2000 };
                CaptainCrashLoopTracker tracker = new CaptainCrashLoopTracker(settings);
                DateTime now = DateTime.UtcNow;
                for (int index = 0; index < 1100; index++)
                {
                    tracker.Record("cpt_" + index, "failure", now, out _);
                }

                AssertTrue(tracker.TrackedCaptainCount <= 1024, "Crash history must evict old captain state at a fixed bound.");
                AssertEqual(256, settings.FailureThreshold, "Threshold must fit within the bounded per-captain history.");
            });

            RunTest("Generation reset cannot consume recreated state", () =>
            {
                CrashLoopDetectionSettings settings = new CrashLoopDetectionSettings { FailureThreshold = 2 };
                CaptainCrashLoopTracker tracker = new CaptainCrashLoopTracker(settings);
                DateTime now = DateTime.UtcNow;
                tracker.Record("cpt_test", "first", now, out _, out long oldGeneration);
                tracker.Reset("cpt_test");
                tracker.Record("cpt_test", "recreated", now, out int count, out long newGeneration);

                AssertFalse(tracker.ResetIfGeneration("cpt_test", oldGeneration), "An old generation must not reset recreated state.");
                AssertEqual(1, count, "Recreated state must remain available.");
                AssertTrue(newGeneration > oldGeneration, "Generations must be globally monotonic.");
            });

            RunTest("Later failure survives an earlier generation reset", () =>
            {
                CrashLoopDetectionSettings settings = new CrashLoopDetectionSettings { FailureThreshold = 2 };
                CaptainCrashLoopTracker tracker = new CaptainCrashLoopTracker(settings);
                DateTime now = DateTime.UtcNow;
                tracker.Record("cpt_test", "first", now, out _, out long generation);
                tracker.Record("cpt_test", "second", now, out int count, out _);

                AssertFalse(tracker.ResetIfGeneration("cpt_test", generation), "A later failure must block consumption of the old generation.");
                AssertEqual(2, count, "The later failure must remain counted.");
            });

            return Task.CompletedTask;
        }
    }
}
