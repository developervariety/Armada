namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using SyslogLogging;

    /// <summary>Tests for CaptainHealthMonitor crash-loop detection and bench deadlines.</summary>
    public class CaptainHealthMonitorTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Captain Health Monitor";

        private static CaptainHealthMonitor CreateMonitor(CrashLoopDetectionSettings? settings = null)
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            CrashLoopDetectionSettings effectiveSettings = settings ?? new CrashLoopDetectionSettings();
            return new CaptainHealthMonitor(effectiveSettings, logging);
        }

        private static CaptainHealthDecision RecordNearInstantExit(CaptainHealthMonitor monitor, string captainId, int failureIndex)
        {
            return monitor.RecordExit(captainId, AgentRuntimeEnum.Cursor, 1, 1000L + failureIndex);
        }

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("CaptainStateEnum_Benched_RoundTripsJson", () =>
            {
                string json = JsonSerializer.Serialize(CaptainStateEnum.Benched);
                AssertEqual("\"Benched\"", json, "Benched should serialize as string");
                CaptainStateEnum deserialized = JsonSerializer.Deserialize<CaptainStateEnum>(json);
                AssertEqual(CaptainStateEnum.Benched, deserialized, "Benched should deserialize");
                return Task.CompletedTask;
            });

            await RunTest("RecordExit_ConsecutiveNearInstantFailures_BenchesAtThreshold", () =>
            {
                CaptainHealthMonitor monitor = CreateMonitor();
                string captainId = "cpt_test_threshold";

                CaptainHealthDecision first = RecordNearInstantExit(monitor, captainId, 0);
                AssertFalse(first.ShouldBench, "First failure should not bench");
                AssertEqual(1, first.ConsecutiveInstantFailures, "First failure count");

                CaptainHealthDecision second = RecordNearInstantExit(monitor, captainId, 1);
                AssertFalse(second.ShouldBench, "Second failure should not bench");
                AssertEqual(2, second.ConsecutiveInstantFailures, "Second failure count");

                CaptainHealthDecision third = RecordNearInstantExit(monitor, captainId, 2);
                AssertTrue(third.ShouldBench, "Third failure should bench at default threshold");
                AssertEqual(3, third.ConsecutiveInstantFailures, "Third failure count");
                AssertContains("3 consecutive near-instant exit-1 launches", third.Reason, "Reason should describe threshold");
                AssertContains("suspected usage limit", third.Reason, "Reason should mention usage limit");
                return Task.CompletedTask;
            });

            await RunTest("RecordExit_HealthyExit_ResetsCounter", () =>
            {
                CaptainHealthMonitor monitor = CreateMonitor();
                string captainId = "cpt_test_reset";

                RecordNearInstantExit(monitor, captainId, 0);
                RecordNearInstantExit(monitor, captainId, 1);

                CaptainHealthDecision healthyExit = monitor.RecordExit(captainId, AgentRuntimeEnum.Cursor, 0, 1000L);
                AssertFalse(healthyExit.ShouldBench, "Healthy exit should not bench");
                AssertEqual(0, healthyExit.ConsecutiveInstantFailures, "Healthy exit resets counter");

                CaptainHealthDecision afterReset = RecordNearInstantExit(monitor, captainId, 3);
                AssertFalse(afterReset.ShouldBench, "First failure after reset should not bench");
                AssertEqual(1, afterReset.ConsecutiveInstantFailures, "Counter should restart at 1");

                CaptainHealthDecision slowExit = monitor.RecordExit(captainId, AgentRuntimeEnum.Cursor, 1, 6000L);
                AssertFalse(slowExit.ShouldBench, "Slow exit-1 should not bench");
                AssertEqual(0, slowExit.ConsecutiveInstantFailures, "Slow exit resets counter");
                return Task.CompletedTask;
            });

            await RunTest("RecordExit_NonExitCodeOne_DoesNotIncrement", () =>
            {
                CaptainHealthMonitor monitor = CreateMonitor();
                string captainId = "cpt_test_exit_code";

                CaptainHealthDecision nullExit = monitor.RecordExit(captainId, AgentRuntimeEnum.Cursor, null, 100L);
                AssertEqual(0, nullExit.ConsecutiveInstantFailures, "Null exit code should not increment");

                CaptainHealthDecision exitTwo = monitor.RecordExit(captainId, AgentRuntimeEnum.Cursor, 2, 100L);
                AssertEqual(0, exitTwo.ConsecutiveInstantFailures, "Exit code 2 should not increment");

                CaptainHealthDecision afterNearInstant = RecordNearInstantExit(monitor, captainId, 0);
                AssertEqual(1, afterNearInstant.ConsecutiveInstantFailures, "Exit code 1 should increment after non-failures");
                return Task.CompletedTask;
            });

            await RunTest("RecordExit_RuntimeAtCeiling_IsNotNearInstant", () =>
            {
                CaptainHealthMonitor monitor = CreateMonitor();
                string captainId = "cpt_test_boundary";

                CaptainHealthDecision atCeiling = monitor.RecordExit(captainId, AgentRuntimeEnum.Cursor, 1, 5000L);
                AssertFalse(atCeiling.ShouldBench, "Runtime at ceiling should not bench");
                AssertEqual(0, atCeiling.ConsecutiveInstantFailures, "Runtime at ceiling should not increment");
                return Task.CompletedTask;
            });

            await RunTest("RecordExit_Disabled_NeverBenches", () =>
            {
                CrashLoopDetectionSettings settings = new CrashLoopDetectionSettings { Enabled = false };
                CaptainHealthMonitor monitor = CreateMonitor(settings);
                string captainId = "cpt_test_disabled";

                for (int i = 0; i < 5; i++)
                {
                    CaptainHealthDecision decision = RecordNearInstantExit(monitor, captainId, i);
                    AssertFalse(decision.ShouldBench, "Disabled monitor should never bench");
                    AssertEqual(0, decision.ConsecutiveInstantFailures, "Disabled monitor should report zero failures");
                }

                return Task.CompletedTask;
            });

            await RunTest("BenchLifecycle_HonorsDeadlineAndClearBench", () =>
            {
                CaptainHealthMonitor monitor = CreateMonitor();
                string captainId = "cpt_test_bench";
                DateTime now = new DateTime(2026, 6, 13, 12, 0, 0, DateTimeKind.Utc);
                DateTime future = now.AddMinutes(5);

                monitor.MarkBenched(captainId, future);
                AssertTrue(monitor.IsBenched(captainId), "Captain should be benched before deadline");

                IReadOnlyList<string> notElapsed = monitor.GetElapsedBenched(now);
                AssertEqual(0, notElapsed.Count, "Future deadline should not be elapsed");

                IReadOnlyList<string> elapsed = monitor.GetElapsedBenched(future);
                AssertEqual(1, elapsed.Count, "Deadline at boundary should be elapsed");
                AssertEqual(captainId, elapsed[0], "Elapsed captain id");
                AssertTrue(monitor.IsBenched(captainId), "IsBenched remains true until restore sweep clears");

                RecordNearInstantExit(monitor, captainId, 0);
                RecordNearInstantExit(monitor, captainId, 1);
                monitor.ClearBench(captainId);
                AssertFalse(monitor.IsBenched(captainId), "ClearBench removes bench state");

                CaptainHealthDecision afterClear = RecordNearInstantExit(monitor, captainId, 2);
                AssertEqual(1, afterClear.ConsecutiveInstantFailures, "ClearBench resets failure counter");
                return Task.CompletedTask;
            });

            await RunTest("Reset_OnlyClearsFailureCounter", () =>
            {
                CaptainHealthMonitor monitor = CreateMonitor();
                string captainId = "cpt_test_reset_only";
                DateTime future = DateTime.UtcNow.AddMinutes(10);

                RecordNearInstantExit(monitor, captainId, 0);
                monitor.MarkBenched(captainId, future);
                monitor.Reset(captainId);

                CaptainHealthDecision afterReset = RecordNearInstantExit(monitor, captainId, 1);
                AssertEqual(1, afterReset.ConsecutiveInstantFailures, "Reset should clear failure counter");
                AssertTrue(monitor.IsBenched(captainId), "Reset should not clear bench deadline");
                return Task.CompletedTask;
            });

            await RunTest("CrashLoopDetectionSettings_ClampOutOfRangeValues", () =>
            {
                CrashLoopDetectionSettings settings = new CrashLoopDetectionSettings
                {
                    FailureThreshold = 1,
                    MaxRuntimeSeconds = 0,
                    CooldownSeconds = 10
                };

                AssertEqual(2, settings.FailureThreshold, "FailureThreshold floor is 2");
                AssertEqual(1, settings.MaxRuntimeSeconds, "MaxRuntimeSeconds floor is 1");
                AssertEqual(30, settings.CooldownSeconds, "CooldownSeconds floor is 30");

                settings.MaxRuntimeSeconds = 120;
                settings.CooldownSeconds = 5000;
                AssertEqual(60, settings.MaxRuntimeSeconds, "MaxRuntimeSeconds ceiling is 60");
                AssertEqual(3600, settings.CooldownSeconds, "CooldownSeconds ceiling is 3600");
                return Task.CompletedTask;
            });

            await RunTest("RecordExit_DifferentCaptains_KeepIndependentCounters", () =>
            {
                CaptainHealthMonitor monitor = CreateMonitor();

                RecordNearInstantExit(monitor, "cpt_a", 0);
                RecordNearInstantExit(monitor, "cpt_a", 1);
                CaptainHealthDecision captainA = RecordNearInstantExit(monitor, "cpt_a", 2);

                CaptainHealthDecision captainB = RecordNearInstantExit(monitor, "cpt_b", 0);

                AssertTrue(captainA.ShouldBench, "Captain A should reach threshold");
                AssertEqual(1, captainB.ConsecutiveInstantFailures, "Captain B should have independent counter");
                return Task.CompletedTask;
            });

            await RunTest("RecordExit_BenchedCaptain_DoesNotThrow", () =>
            {
                CaptainHealthMonitor monitor = CreateMonitor();
                string captainId = "cpt_test_benched_record";
                monitor.MarkBenched(captainId, DateTime.UtcNow.AddMinutes(5));

                CaptainHealthDecision decision = RecordNearInstantExit(monitor, captainId, 0);
                AssertEqual(1, decision.ConsecutiveInstantFailures, "Benched captain can still record exits");
                return Task.CompletedTask;
            });
        }
    }
}
