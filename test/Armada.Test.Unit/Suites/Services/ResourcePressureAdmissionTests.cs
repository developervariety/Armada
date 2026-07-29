namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using SyslogLogging;

    /// <summary>Tests for the resource-pressure admission policy and OOM classification.</summary>
    public class ResourcePressureAdmissionTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Resource Pressure Admission";

        private sealed class FakeProbe : IResourcePressureProbe
        {
            public long? AvailableMemoryBytes;
            public ResourcePressureSnapshot Probe()
            {
                return new ResourcePressureSnapshot { AvailableMemoryBytes = AvailableMemoryBytes };
            }
        }

        private sealed class FakeClock
        {
            public DateTime Now = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            public DateTime GetNow() { return Now; }
        }

        private static LoggingModule CreateLogging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }

        private static ResourcePressureAdmission CreateAdmission(
            ResourcePressureAdmissionSettings settings,
            FakeProbe probe,
            FakeClock clock)
        {
            return new ResourcePressureAdmission(settings, probe, CreateLogging(), clock.GetNow);
        }

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("Admission_WindowAlwaysAdmitsWhenDisabled", () =>
            {
                ResourcePressureAdmissionSettings settings = new ResourcePressureAdmissionSettings { Enabled = false, MinAvailableMemoryMb = 1024 };
                FakeProbe probe = new FakeProbe { AvailableMemoryBytes = 1L };
                ResourcePressureAdmission admission = CreateAdmission(settings, probe, new FakeClock());

                ResourcePressureDecision decision = admission.Evaluate(0);

                AssertTrue(decision.Admit, "Disabled admission must always admit");
                AssertEqual(String.Empty, decision.Reason, "Disabled admission reason must be empty");
                return Task.CompletedTask;
            });

            await RunTest("Admission_AvailableMemoryBelowThreshold_DeferredsWithClearReason", () =>
            {
                ResourcePressureAdmissionSettings settings = new ResourcePressureAdmissionSettings { Enabled = true, MinAvailableMemoryMb = 512, MaxConcurrentBuilds = 0 };
                FakeProbe probe = new FakeProbe { AvailableMemoryBytes = 64L * 1024L * 1024L };
                ResourcePressureAdmission admission = CreateAdmission(settings, probe, new FakeClock());

                ResourcePressureDecision decision = admission.Evaluate(0);

                AssertFalse(decision.Admit, "Low available memory must defer");
                AssertContains("available memory", decision.Reason, "Deferred reason must mention available memory");
                AssertContains("below minimum", decision.Reason, "Deferred reason must mention the threshold");
                AssertNotNull(decision.Snapshot, "Deferred decision must carry a telemetry snapshot");
                AssertEqual(probe.AvailableMemoryBytes, decision.Snapshot.AvailableMemoryBytes, "Snapshot must reflect the probe");
                return Task.CompletedTask;
            });

            await RunTest("Admission_AvailableMemoryAboveThreshold_Admitted", () =>
            {
                ResourcePressureAdmissionSettings settings = new ResourcePressureAdmissionSettings { Enabled = true, MinAvailableMemoryMb = 512, MaxConcurrentBuilds = 0 };
                FakeProbe probe = new FakeProbe { AvailableMemoryBytes = 2L * 1024L * 1024L * 1024L };
                ResourcePressureAdmission admission = CreateAdmission(settings, probe, new FakeClock());

                ResourcePressureDecision decision = admission.Evaluate(0);

                AssertTrue(decision.Admit, "Plenty of memory must admit");
                AssertEqual(String.Empty, decision.Reason, "Admitted reason must be empty");
                return Task.CompletedTask;
            });

            await RunTest("Admission_TypelessMemorySnapshot_Admitted", () =>
            {
                ResourcePressureAdmissionSettings settings = new ResourcePressureAdmissionSettings { Enabled = true, MinAvailableMemoryMb = 512 };
                FakeProbe probe = new FakeProbe { AvailableMemoryBytes = null };
                ResourcePressureAdmission admission = CreateAdmission(settings, probe, new FakeClock());

                ResourcePressureDecision decision = admission.Evaluate(0);

                AssertTrue(decision.Admit, "Unknown memory must not block admission (cannot infer pressure)");
                return Task.CompletedTask;
            });

            await RunTest("Admission_BuildPressureAtMax_DeferredsWithClearReason", () =>
            {
                ResourcePressureAdmissionSettings settings = new ResourcePressureAdmissionSettings { Enabled = true, MinAvailableMemoryMb = 0, MaxConcurrentBuilds = 2 };
                FakeProbe probe = new FakeProbe { AvailableMemoryBytes = 8L * 1024L * 1024L * 1024L };
                ResourcePressureAdmission admission = CreateAdmission(settings, probe, new FakeClock());

                ResourcePressureDecision decision = admission.Evaluate(2);

                AssertFalse(decision.Admit, "Pressure at the max must defer");
                AssertContains("build/captain pressure", decision.Reason, "Deferred reason must mention build/captain pressure");
                AssertContains("reached max 2", decision.Reason, "Deferred reason must mention the max");
                return Task.CompletedTask;
            });

            await RunTest("Admission_BuildPressureBelowMax_Admitted", () =>
            {
                ResourcePressureAdmissionSettings settings = new ResourcePressureAdmissionSettings { Enabled = true, MinAvailableMemoryMb = 0, MaxConcurrentBuilds = 2 };
                FakeProbe probe = new FakeProbe { AvailableMemoryBytes = 8L * 1024L * 1024L * 1024L };
                ResourcePressureAdmission admission = CreateAdmission(settings, probe, new FakeClock());

                ResourcePressureDecision decision = admission.Evaluate(1);

                AssertTrue(decision.Admit, "Pressure below the max must admit");
                return Task.CompletedTask;
            });

            await RunTest("OOM_MarkOom_SuspendsAdmissionUntilCapacityReturns", () =>
            {
                ResourcePressureAdmissionSettings settings = new ResourcePressureAdmissionSettings { Enabled = true, MinAvailableMemoryMb = 512, OomCooldownSeconds = 120 };
                FakeProbe probe = new FakeProbe { AvailableMemoryBytes = 8L * 1024L * 1024L * 1024L };
                FakeClock clock = new FakeClock();
                ResourcePressureAdmission admission = CreateAdmission(settings, probe, clock);

                AssertFalse(admission.IsCapacitySuspended(), "No OOM yet means no suspension");

                admission.MarkOom();

                AssertTrue(admission.IsCapacitySuspended(), "Immediately after MarkOom capacity must be suspended");
                ResourcePressureDecision deferred = admission.Evaluate(1);
                AssertFalse(deferred.Admit, "Admission during OOM cooldown must defer");
                AssertContains("captain OOM (exit 137)", deferred.Reason, "Deferred reason must classify the OOM cooldown");
                AssertContains("cooldown until", deferred.Reason, "Deferred reason must carry the cooldown timestamp");
                AssertNotNull(deferred.Snapshot, "Deferred telemetry must carry a snapshot");

                // Capacity returns once the cooldown has elapsed AND the probe still reports memory.
                clock.Now = clock.Now.AddSeconds(settings.OomCooldownSeconds + 1);
                AssertFalse(admission.IsCapacitySuspended(), "Past the cooldown, capacity is no longer suspended");
                ResourcePressureDecision admitted = admission.Evaluate(0);
                AssertTrue(admitted.Admit, "After capacity returns the mission must be admitted again");
                return Task.CompletedTask;
            });

            await RunTest("OOM_CapacityRelease_UnderMemoryPressure_StillDeferred", () =>
            {
                ResourcePressureAdmissionSettings settings = new ResourcePressureAdmissionSettings { Enabled = true, MinAvailableMemoryMb = 512, OomCooldownSeconds = 60 };
                FakeProbe probe = new FakeProbe { AvailableMemoryBytes = 8L * 1024L * 1024L * 1024L };
                FakeClock clock = new FakeClock();
                ResourcePressureAdmission admission = CreateAdmission(settings, probe, clock);

                admission.MarkOom();
                // Cooldown elapses but memory has NOT recovered -> still deferred (by memory gate), capacity not released.
                clock.Now = clock.Now.AddSeconds(settings.OomCooldownSeconds + 1);
                probe.AvailableMemoryBytes = 4L * 1024L * 1024L;

                ResourcePressureDecision decision = admission.Evaluate(0);
                AssertFalse(decision.Admit, "Memory still under threshold must defer even after OOM cooldown");
                AssertContains("available memory", decision.Reason, "Reason must reflect memory pressure, not OOM cooldown");
                AssertFalse(admission.IsCapacitySuspended(), "OOM suspension cleared once cooldown elapsed");
                return Task.CompletedTask;
            });

            await RunTest("OOM_ExitCodeClassification_OnlyExit137", () =>
            {
                AssertTrue(AdmiralService.IsExitCodeOom(137), "Exit 137 must classify as OOM");
                AssertFalse(AdmiralService.IsExitCodeOom(0), "Exit 0 must not classify as OOM");
                AssertFalse(AdmiralService.IsExitCodeOom(1), "Exit 1 must not classify as OOM");
                AssertFalse(AdmiralService.IsExitCodeOom(9), "Exit 9 must not classify as OOM");
                AssertFalse(AdmiralService.IsExitCodeOom(null), "Null exit must not classify as OOM");

                string reason = AdmiralService.BuildOomFailureReason(137);
                AssertContains("OOM:", reason, "OOM reason must carry the stable OOM: marker");
                AssertContains("exit 137", reason, "OOM reason must record the exit code");
                AssertContains("retry deferred until resource capacity returns", reason, "OOM reason must state retry-gating intent");
                return Task.CompletedTask;
            });

            await RunTest("OOM_RepeatedMarkOom_ExtendsCooldown", () =>
            {
                ResourcePressureAdmissionSettings settings = new ResourcePressureAdmissionSettings { Enabled = true, MinAvailableMemoryMb = 0, OomCooldownSeconds = 60 };
                FakeProbe probe = new FakeProbe { AvailableMemoryBytes = 8L * 1024L * 1024L * 1024L };
                FakeClock clock = new FakeClock();
                ResourcePressureAdmission admission = CreateAdmission(settings, probe, clock);

                admission.MarkOom();
                DateTime firstWindow = clock.Now.AddSeconds(settings.OomCooldownSeconds);

                clock.Now = clock.Now.AddSeconds(30);
                admission.MarkOom();
                DateTime secondWindow = clock.Now.AddSeconds(settings.OomCooldownSeconds);

                AssertTrue(secondWindow > firstWindow, "A later MarkOom must extend the cooldown window");
                AssertTrue(admission.IsCapacitySuspended(), "Suspension must persist after a refreshed MarkOom");
                return Task.CompletedTask;
            });
        }
    }
}