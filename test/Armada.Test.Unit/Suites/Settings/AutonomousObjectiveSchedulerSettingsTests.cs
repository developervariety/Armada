namespace Armada.Test.Unit.Suites.Settings
{
    using System.Threading.Tasks;
    using Armada.Core.Settings;
    using Armada.Test.Common;

    public class AutonomousObjectiveSchedulerSettingsTests : TestSuite
    {
        public override string Name => "AutonomousObjectiveSchedulerSettings";

        protected override async Task RunTestsAsync()
        {
            await RunTest("Defaults_Enabled_IsFalse", () =>
            {
                AutonomousObjectiveSchedulerSettings s = new AutonomousObjectiveSchedulerSettings();
                AssertFalse(s.Enabled);
                return Task.CompletedTask;
            });

            await RunTest("Defaults_IntervalMinutes_Is25", () =>
            {
                AutonomousObjectiveSchedulerSettings s = new AutonomousObjectiveSchedulerSettings();
                AssertEqual(25, s.IntervalMinutes);
                return Task.CompletedTask;
            });

            await RunTest("Defaults_MaxConcurrentVoyages_IsAtLeast1", () =>
            {
                AutonomousObjectiveSchedulerSettings s = new AutonomousObjectiveSchedulerSettings();
                AssertTrue(s.MaxConcurrentVoyages >= 1);
                return Task.CompletedTask;
            });

            await RunTest("Defaults_Paused_IsFalse", () =>
            {
                AutonomousObjectiveSchedulerSettings s = new AutonomousObjectiveSchedulerSettings();
                AssertFalse(s.Paused);
                return Task.CompletedTask;
            });

            await RunTest("IntervalMinutes_Zero_ClampsTo1", () =>
            {
                AutonomousObjectiveSchedulerSettings s = new AutonomousObjectiveSchedulerSettings
                {
                    IntervalMinutes = 0
                };
                AssertEqual(1, s.IntervalMinutes);
                return Task.CompletedTask;
            });

            await RunTest("IntervalMinutes_Negative_ClampsTo1", () =>
            {
                AutonomousObjectiveSchedulerSettings s = new AutonomousObjectiveSchedulerSettings
                {
                    IntervalMinutes = -999
                };
                AssertEqual(1, s.IntervalMinutes);
                return Task.CompletedTask;
            });

            await RunTest("IntervalMinutes_OverMax_ClampsToCap", () =>
            {
                AutonomousObjectiveSchedulerSettings s = new AutonomousObjectiveSchedulerSettings
                {
                    IntervalMinutes = 99999
                };
                AssertEqual(1440, s.IntervalMinutes);
                return Task.CompletedTask;
            });

            await RunTest("MaxConcurrentVoyages_Zero_ClampsTo1", () =>
            {
                AutonomousObjectiveSchedulerSettings s = new AutonomousObjectiveSchedulerSettings
                {
                    MaxConcurrentVoyages = 0
                };
                AssertEqual(1, s.MaxConcurrentVoyages);
                return Task.CompletedTask;
            });

            await RunTest("MaxConcurrentVoyages_Negative_ClampsTo1", () =>
            {
                AutonomousObjectiveSchedulerSettings s = new AutonomousObjectiveSchedulerSettings
                {
                    MaxConcurrentVoyages = -5
                };
                AssertEqual(1, s.MaxConcurrentVoyages);
                return Task.CompletedTask;
            });

            await RunTest("MaxConcurrentVoyages_OverCap_ClampsTo100", () =>
            {
                AutonomousObjectiveSchedulerSettings s = new AutonomousObjectiveSchedulerSettings
                {
                    MaxConcurrentVoyages = 999
                };
                AssertEqual(100, s.MaxConcurrentVoyages);
                return Task.CompletedTask;
            });

            await RunTest("ArmadaSettings_AutonomousObjectiveScheduler_NonNullByDefault", () =>
            {
                ArmadaSettings settings = new ArmadaSettings();
                AssertNotNull(settings.AutonomousObjectiveScheduler);
                return Task.CompletedTask;
            });

            await RunTest("ArmadaSettings_AutonomousObjectiveScheduler_NullAssign_SetsDefault", () =>
            {
                ArmadaSettings settings = new ArmadaSettings
                {
                    AutonomousObjectiveScheduler = null
                };
                AssertNotNull(settings.AutonomousObjectiveScheduler);
                return Task.CompletedTask;
            });
        }
    }
}
