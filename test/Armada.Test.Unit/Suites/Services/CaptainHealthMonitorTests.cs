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

            await RunTest("RecordExit_CustomFailureThreshold_BenchesAtConfiguredValue", () =>
            {
                CrashLoopDetectionSettings settings = new CrashLoopDetectionSettings { FailureThreshold = 2 };
                CaptainHealthMonitor monitor = CreateMonitor(settings);
                string captainId = "cpt_custom_threshold";

                CaptainHealthDecision first = RecordNearInstantExit(monitor, captainId, 0);
                AssertFalse(first.ShouldBench, "First failure should not bench at threshold 2");

                CaptainHealthDecision second = RecordNearInstantExit(monitor, captainId, 1);
                AssertTrue(second.ShouldBench, "Second failure should bench at threshold 2 (benching honors configured threshold, not a hardcoded 3)");
                AssertEqual(2, second.ConsecutiveInstantFailures, "Counter at configured threshold");
                AssertContains("2 consecutive near-instant exit-1 launches", second.Reason, "Reason reflects configured threshold count");
                return Task.CompletedTask;
            });

            await RunTest("RecordExit_FailuresPastThreshold_StayBenchedAndKeepCounting", () =>
            {
                CaptainHealthMonitor monitor = CreateMonitor();
                string captainId = "cpt_past_threshold";

                RecordNearInstantExit(monitor, captainId, 0);
                RecordNearInstantExit(monitor, captainId, 1);
                RecordNearInstantExit(monitor, captainId, 2);

                CaptainHealthDecision fourth = RecordNearInstantExit(monitor, captainId, 3);
                AssertTrue(fourth.ShouldBench, "Failures past the threshold remain ShouldBench");
                AssertEqual(4, fourth.ConsecutiveInstantFailures, "Counter keeps climbing past the threshold");
                AssertContains("4 consecutive near-instant exit-1 launches", fourth.Reason, "Reason reflects the current (climbing) count");
                return Task.CompletedTask;
            });

            await RunTest("RecordExit_BenchReason_IncludesRuntimeCeilingMs", () =>
            {
                CaptainHealthMonitor monitor = CreateMonitor();
                string captainId = "cpt_reason_ms";

                RecordNearInstantExit(monitor, captainId, 0);
                RecordNearInstantExit(monitor, captainId, 1);
                CaptainHealthDecision third = RecordNearInstantExit(monitor, captainId, 2);

                AssertContains("runtime < 5000 ms", third.Reason, "Reason includes the near-instant ceiling in ms (MaxRuntimeSeconds * 1000)");
                return Task.CompletedTask;
            });

            await RunTest("RecordExit_RuntimeZero_IsNearInstant", () =>
            {
                CaptainHealthMonitor monitor = CreateMonitor();
                string captainId = "cpt_runtime_zero";

                CaptainHealthDecision decision = monitor.RecordExit(captainId, AgentRuntimeEnum.Cursor, 1, 0L);
                AssertEqual(1, decision.ConsecutiveInstantFailures, "Runtime of 0 ms (strictly below ceiling) counts as near-instant");
                return Task.CompletedTask;
            });

            await RunTest("RecordExit_PropagatesRuntimeOntoDecision", () =>
            {
                CaptainHealthMonitor monitor = CreateMonitor();

                CaptainHealthDecision claude = monitor.RecordExit("cpt_runtime_field", AgentRuntimeEnum.ClaudeCode, 1, 100L);
                AssertEqual(AgentRuntimeEnum.ClaudeCode, claude.Runtime, "Decision echoes the runtime that exited");

                CrashLoopDetectionSettings disabled = new CrashLoopDetectionSettings { Enabled = false };
                CaptainHealthMonitor disabledMonitor = CreateMonitor(disabled);
                CaptainHealthDecision disabledDecision = disabledMonitor.RecordExit("cpt_runtime_field2", AgentRuntimeEnum.Codex, 1, 100L);
                AssertEqual(AgentRuntimeEnum.Codex, disabledDecision.Runtime, "Disabled early-return path still echoes the runtime");
                return Task.CompletedTask;
            });

            await RunTest("PublicMethods_EmptyCaptainId_Throw", () =>
            {
                CaptainHealthMonitor monitor = CreateMonitor();

                AssertThrows<ArgumentException>(() => monitor.RecordExit(string.Empty, AgentRuntimeEnum.Cursor, 1, 100L), "RecordExit rejects empty captainId");
                AssertThrows<ArgumentException>(() => monitor.MarkBenched(string.Empty, DateTime.UtcNow), "MarkBenched rejects empty captainId");
                AssertThrows<ArgumentException>(() => monitor.IsBenched(string.Empty), "IsBenched rejects empty captainId");
                AssertThrows<ArgumentException>(() => monitor.ClearBench(string.Empty), "ClearBench rejects empty captainId");
                AssertThrows<ArgumentException>(() => monitor.Reset(string.Empty), "Reset rejects empty captainId");
                return Task.CompletedTask;
            });

            await RunTest("GetElapsedBenched_MultipleCaptains_ReturnsOnlyElapsed", () =>
            {
                CaptainHealthMonitor monitor = CreateMonitor();
                DateTime now = new DateTime(2026, 6, 13, 12, 0, 0, DateTimeKind.Utc);

                AssertEqual(0, monitor.GetElapsedBenched(now).Count, "No benched captains yields an empty list");

                monitor.MarkBenched("cpt_elapsed", now.AddMinutes(-1));
                monitor.MarkBenched("cpt_boundary", now);
                monitor.MarkBenched("cpt_future", now.AddMinutes(5));

                IReadOnlyList<string> elapsed = monitor.GetElapsedBenched(now);
                AssertEqual(2, elapsed.Count, "Only past-and-at-boundary deadlines are elapsed");
                AssertTrue(elapsed.Contains("cpt_elapsed"), "Past deadline is elapsed");
                AssertTrue(elapsed.Contains("cpt_boundary"), "Deadline exactly at now is elapsed");
                AssertFalse(elapsed.Contains("cpt_future"), "Future deadline is not elapsed");
                return Task.CompletedTask;
            });

            await RunTest("ClearBenchAndReset_UnknownCaptain_DoNotThrow", () =>
            {
                CaptainHealthMonitor monitor = CreateMonitor();

                monitor.ClearBench("cpt_never_seen");
                monitor.Reset("cpt_never_seen");
                AssertFalse(monitor.IsBenched("cpt_never_seen"), "Unknown captain is not benched after no-op clear/reset");
                return Task.CompletedTask;
            });

            await RunTest("MarkBenched_CalledTwice_OverwritesDeadline", () =>
            {
                CaptainHealthMonitor monitor = CreateMonitor();
                string captainId = "cpt_overwrite";
                DateTime now = new DateTime(2026, 6, 13, 12, 0, 0, DateTimeKind.Utc);

                monitor.MarkBenched(captainId, now.AddMinutes(10));
                AssertEqual(0, monitor.GetElapsedBenched(now).Count, "Initial future deadline is not elapsed");

                monitor.MarkBenched(captainId, now.AddMinutes(-1));
                IReadOnlyList<string> elapsed = monitor.GetElapsedBenched(now);
                AssertEqual(1, elapsed.Count, "Second MarkBenched overwrites the deadline (now in the past)");
                AssertEqual(captainId, elapsed[0], "Overwritten captain id");
                return Task.CompletedTask;
            });
        }
    }
}
