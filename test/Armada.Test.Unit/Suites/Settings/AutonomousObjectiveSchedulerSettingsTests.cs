namespace Armada.Test.Unit.Suites.Settings
{
    using System;
    using System.IO;
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

            await RunTest("Defaults_MaxConcurrentVoyages_Is1", () =>
            {
                AutonomousObjectiveSchedulerSettings s = new AutonomousObjectiveSchedulerSettings();
                AssertEqual(1, s.MaxConcurrentVoyages);
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

            await RunTest("IntervalMinutes_AtMin_Preserves1", () =>
            {
                AutonomousObjectiveSchedulerSettings s = new AutonomousObjectiveSchedulerSettings
                {
                    IntervalMinutes = 1
                };
                AssertEqual(1, s.IntervalMinutes);
                return Task.CompletedTask;
            });

            await RunTest("IntervalMinutes_AtMax_Preserves1440", () =>
            {
                AutonomousObjectiveSchedulerSettings s = new AutonomousObjectiveSchedulerSettings
                {
                    IntervalMinutes = 1440
                };
                AssertEqual(1440, s.IntervalMinutes);
                return Task.CompletedTask;
            });

            await RunTest("IntervalMinutes_InRange_PreservesValue", () =>
            {
                AutonomousObjectiveSchedulerSettings s = new AutonomousObjectiveSchedulerSettings
                {
                    IntervalMinutes = 60
                };
                AssertEqual(60, s.IntervalMinutes);
                return Task.CompletedTask;
            });

            await RunTest("MaxConcurrentVoyages_AtMin_Preserves1", () =>
            {
                AutonomousObjectiveSchedulerSettings s = new AutonomousObjectiveSchedulerSettings
                {
                    MaxConcurrentVoyages = 1
                };
                AssertEqual(1, s.MaxConcurrentVoyages);
                return Task.CompletedTask;
            });

            await RunTest("MaxConcurrentVoyages_AtMax_Preserves100", () =>
            {
                AutonomousObjectiveSchedulerSettings s = new AutonomousObjectiveSchedulerSettings
                {
                    MaxConcurrentVoyages = 100
                };
                AssertEqual(100, s.MaxConcurrentVoyages);
                return Task.CompletedTask;
            });

            await RunTest("MaxConcurrentVoyages_InRange_PreservesValue", () =>
            {
                AutonomousObjectiveSchedulerSettings s = new AutonomousObjectiveSchedulerSettings
                {
                    MaxConcurrentVoyages = 5
                };
                AssertEqual(5, s.MaxConcurrentVoyages);
                return Task.CompletedTask;
            });

            await RunTest("Enabled_CanBeSetTrue", () =>
            {
                AutonomousObjectiveSchedulerSettings s = new AutonomousObjectiveSchedulerSettings
                {
                    Enabled = true
                };
                AssertTrue(s.Enabled);
                return Task.CompletedTask;
            });

            await RunTest("Paused_CanBeSetTrue", () =>
            {
                AutonomousObjectiveSchedulerSettings s = new AutonomousObjectiveSchedulerSettings
                {
                    Paused = true
                };
                AssertTrue(s.Paused);
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

            await RunTest("ArmadaSettings_AutonomousObjectiveScheduler_CustomInstance_Preserved", () =>
            {
                AutonomousObjectiveSchedulerSettings custom = new AutonomousObjectiveSchedulerSettings
                {
                    Enabled = true,
                    IntervalMinutes = 90,
                    MaxConcurrentVoyages = 3,
                    Paused = true
                };
                ArmadaSettings settings = new ArmadaSettings
                {
                    AutonomousObjectiveScheduler = custom
                };
                AssertEqual(custom, settings.AutonomousObjectiveScheduler);
                AssertTrue(settings.AutonomousObjectiveScheduler.Enabled);
                AssertEqual(90, settings.AutonomousObjectiveScheduler.IntervalMinutes);
                AssertEqual(3, settings.AutonomousObjectiveScheduler.MaxConcurrentVoyages);
                AssertTrue(settings.AutonomousObjectiveScheduler.Paused);
                return Task.CompletedTask;
            });

            await RunTest("LoadAsync_AutonomousObjectiveScheduler_RoundTrip", async () =>
            {
                string tempFile = Path.Combine(Path.GetTempPath(), "armada_test_aoss_" + Guid.NewGuid().ToString("N") + ".json");
                try
                {
                    ArmadaSettings original = new ArmadaSettings();
                    original.AutonomousObjectiveScheduler = new AutonomousObjectiveSchedulerSettings
                    {
                        Enabled = true,
                        IntervalMinutes = 45,
                        MaxConcurrentVoyages = 4,
                        Paused = true
                    };
                    await original.SaveAsync(tempFile).ConfigureAwait(false);

                    ArmadaSettings loaded = await ArmadaSettings.LoadAsync(tempFile).ConfigureAwait(false);
                    AssertNotNull(loaded.AutonomousObjectiveScheduler);
                    AssertTrue(loaded.AutonomousObjectiveScheduler.Enabled);
                    AssertEqual(45, loaded.AutonomousObjectiveScheduler.IntervalMinutes);
                    AssertEqual(4, loaded.AutonomousObjectiveScheduler.MaxConcurrentVoyages);
                    AssertTrue(loaded.AutonomousObjectiveScheduler.Paused);
                }
                finally
                {
                    if (File.Exists(tempFile))
                    {
                        File.Delete(tempFile);
                    }
                }
            });

            await RunTest("LoadAsync_AutonomousObjectiveScheduler_OutOfRangeValues_ClampsOnDeserialize", async () =>
            {
                string tempFile = Path.Combine(Path.GetTempPath(), "armada_test_aoss_" + Guid.NewGuid().ToString("N") + ".json");
                try
                {
                    string json = "{\"autonomousObjectiveScheduler\":{\"enabled\":true,\"intervalMinutes\":0,\"maxConcurrentVoyages\":500,\"paused\":false}}";
                    await File.WriteAllTextAsync(tempFile, json).ConfigureAwait(false);

                    ArmadaSettings loaded = await ArmadaSettings.LoadAsync(tempFile).ConfigureAwait(false);
                    AssertNotNull(loaded.AutonomousObjectiveScheduler);
                    AssertTrue(loaded.AutonomousObjectiveScheduler.Enabled);
                    AssertEqual(1, loaded.AutonomousObjectiveScheduler.IntervalMinutes);
                    AssertEqual(100, loaded.AutonomousObjectiveScheduler.MaxConcurrentVoyages);
                    AssertFalse(loaded.AutonomousObjectiveScheduler.Paused);
                }
                finally
                {
                    if (File.Exists(tempFile))
                    {
                        File.Delete(tempFile);
                    }
                }
            });

            await RunTest("LoadAsync_MissingAutonomousObjectiveScheduler_UsesDefaults", async () =>
            {
                string tempFile = Path.Combine(Path.GetTempPath(), "armada_test_aoss_" + Guid.NewGuid().ToString("N") + ".json");
                try
                {
                    string json = "{\"admiralPort\":7890}";
                    await File.WriteAllTextAsync(tempFile, json).ConfigureAwait(false);

                    ArmadaSettings loaded = await ArmadaSettings.LoadAsync(tempFile).ConfigureAwait(false);
                    AssertNotNull(loaded.AutonomousObjectiveScheduler);
                    AssertFalse(loaded.AutonomousObjectiveScheduler.Enabled);
                    AssertEqual(25, loaded.AutonomousObjectiveScheduler.IntervalMinutes);
                    AssertEqual(1, loaded.AutonomousObjectiveScheduler.MaxConcurrentVoyages);
                    AssertFalse(loaded.AutonomousObjectiveScheduler.Paused);
                }
                finally
                {
                    if (File.Exists(tempFile))
                    {
                        File.Delete(tempFile);
                    }
                }
            });
        }
    }
}
