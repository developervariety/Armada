namespace Armada.Test.Unit.Suites.Services
{
    using Armada.Core;
    using Armada.Core.Settings;
    using Armada.Test.Common;

    public class SettingsTests : TestSuite
    {
        public override string Name => "Settings";

        protected override async Task RunTestsAsync()
        {
            await RunTest("ArmadaSettings ApplyHotReloadableFrom BranchCleanupSweepIntervalCycles TakesEffectWithoutRestart", () =>
            {
                // The health loop reads the cadence on every cycle and the sweep reads the retention window on
                // every run, so a reloaded value is live without a restart.
                ArmadaSettings live = new ArmadaSettings();
                ArmadaSettings edited = new ArmadaSettings
                {
                    BranchCleanupSweepIntervalCycles = 50,
                    BranchCleanupPreservedRefRetentionDays = 3
                };
                AssertEqual(200, live.BranchCleanupSweepIntervalCycles, "default cadence");
                AssertEqual(14, live.BranchCleanupPreservedRefRetentionDays, "default retention");

                live.ApplyHotReloadableFrom(edited);

                AssertEqual(50, live.BranchCleanupSweepIntervalCycles, "a reloaded branch cleanup cadence is applied");
                AssertEqual(3, live.BranchCleanupPreservedRefRetentionDays, "a reloaded preserved-ref retention is applied");
                return Task.CompletedTask;
            });

            await RunTest("ArmadaSettings ApplyHotReloadableFrom keeps sections held by reference and copies their values", () =>
            {
                // The crash-loop tracker and the definition-of-done gate hold these sections by reference, so a
                // reload must change the values inside the same objects rather than swap the objects.
                ArmadaSettings live = new ArmadaSettings();
                CrashLoopDetectionSettings crashLoop = live.CrashLoopDetection;
                DefinitionOfDoneSettings dod = live.DefinitionOfDone;
                ArmadaSettings edited = new ArmadaSettings();
                edited.CrashLoopDetection.Enabled = false;
                edited.CrashLoopDetection.FailureThreshold = 7;
                edited.CrashLoopDetection.CooldownSeconds = 90;
                edited.CrashLoopDetection.WindowMinutes = 30;
                edited.DefinitionOfDone.Enabled = false;
                edited.DefinitionOfDone.CommandTimeoutSeconds = 120;
                edited.DefinitionOfDone.AppliedPersonas = new List<string> { "Worker", "TestEngineer" };
                edited.DefinitionOfDone.RunConsumerTests = false;
                edited.CodeIndex.StalenessSweepIntervalCycles = 20;

                live.ApplyHotReloadableFrom(edited);

                AssertTrue(Object.ReferenceEquals(crashLoop, live.CrashLoopDetection), "the crash-loop section object must survive a reload");
                AssertTrue(Object.ReferenceEquals(dod, live.DefinitionOfDone), "the definition-of-done section object must survive a reload");
                AssertFalse(crashLoop.Enabled, "crash-loop Enabled is reloaded");
                AssertEqual(7, crashLoop.FailureThreshold, "crash-loop threshold is reloaded");
                AssertEqual(90, crashLoop.CooldownSeconds, "crash-loop cooldown is reloaded");
                AssertEqual(30, crashLoop.WindowMinutes, "crash-loop window is reloaded");
                AssertFalse(dod.Enabled, "definition-of-done Enabled is reloaded");
                AssertEqual(120, dod.CommandTimeoutSeconds, "definition-of-done timeout is reloaded");
                AssertTrue(dod.AppliedPersonas.SequenceEqual(new[] { "Worker", "TestEngineer" }), "definition-of-done personas are reloaded");
                AssertFalse(Object.ReferenceEquals(dod.AppliedPersonas, edited.DefinitionOfDone.AppliedPersonas), "the persona list is copied, not shared");
                AssertFalse(dod.RunConsumerTests, "definition-of-done consumer tests switch is reloaded");
                AssertEqual(20, live.CodeIndex.StalenessSweepIntervalCycles, "the code index staleness cadence is reloaded");
                return Task.CompletedTask;
            });

            await RunTest("ArmadaSettings LoadAsync UnknownKeys AreIgnoredAndNotWrittenBack", async () =>
            {
                string tempFile = Path.Combine(Path.GetTempPath(), "armada_test_settings_" + Guid.NewGuid().ToString("N") + ".json");

                try
                {
                    // Keys the settings model does not declare, scalar and nested, between declared keys.
                    string withUnknownKeys = "{\n"
                        + "  \"admiralPort\": 9123,\n"
                        + "  \"exampleUnknownFlag\": false,\n"
                        + "  \"exampleUnknownThreshold\": 15,\n"
                        + "  \"exampleUnknownRatio\": 0.6,\n"
                        + "  \"exampleUnknownSection\": { \"enabled\": true, \"maxAgeDays\": 30 },\n"
                        + "  \"heartbeatIntervalSeconds\": 45\n"
                        + "}";
                    await File.WriteAllTextAsync(tempFile, withUnknownKeys);

                    ArmadaSettings loaded = await ArmadaSettings.LoadAsync(tempFile);
                    AssertEqual(9123, loaded.AdmiralPort, "keys before the unknown ones still load");
                    AssertEqual(45, loaded.HeartbeatIntervalSeconds, "keys after the unknown ones still load");

                    await loaded.SaveAsync(tempFile);
                    string saved = await File.ReadAllTextAsync(tempFile);
                    AssertFalse(saved.Contains("exampleUnknown", StringComparison.OrdinalIgnoreCase), "a saved settings file must not carry an unknown key");
                }
                finally
                {
                    if (File.Exists(tempFile)) File.Delete(tempFile);
                }
            });

        }
    }
}
