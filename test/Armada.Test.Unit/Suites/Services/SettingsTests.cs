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
            await RunTest("ArmadaSettings DefaultValues AreCorrect", () =>
            {
                ArmadaSettings settings = new ArmadaSettings();
                AssertEqual(Constants.DefaultAdmiralPort, settings.AdmiralPort);
                AssertEqual(Constants.DefaultMcpPort, settings.McpPort);
                AssertEqual(Constants.DefaultHeartbeatIntervalSeconds, settings.HeartbeatIntervalSeconds);
                AssertEqual(Constants.DefaultStallThresholdMinutes, settings.StallThresholdMinutes);
                AssertEqual(Constants.DefaultMaxRecoveryAttempts, settings.MaxRecoveryAttempts);
                AssertEqual(Constants.DefaultMaxLogFileSizeBytes, settings.MaxLogFileSizeBytes);
                AssertEqual(Constants.DefaultMaxLogFileCount, settings.MaxLogFileCount);
                AssertEqual(Constants.DefaultDataRetentionDays, settings.DataRetentionDays);
                AssertFalse(settings.AutoCreatePullRequests);
                AssertNull(settings.ApiKey);
            });

            await RunTest("ArmadaSettings DefaultAgents ContainsExpectedRuntimes", () =>
            {
                ArmadaSettings settings = new ArmadaSettings();
                AssertTrue(settings.Agents.Count >= 2, "Should have at least 2 default agents");
                AssertEqual(Armada.Core.Enums.AgentRuntimeEnum.ClaudeCode, settings.Agents[0].Runtime);
                AssertEqual(Armada.Core.Enums.AgentRuntimeEnum.Codex, settings.Agents[1].Runtime);
            });

            await RunTest("ArmadaSettings SetPort InvalidRange Throws", () =>
            {
                ArmadaSettings settings = new ArmadaSettings();
                AssertThrows<ArgumentOutOfRangeException>(() => settings.AdmiralPort = 0);
                AssertThrows<ArgumentOutOfRangeException>(() => settings.AdmiralPort = 70000);
                AssertThrows<ArgumentOutOfRangeException>(() => settings.McpPort = -1);
            });

            await RunTest("ArmadaSettings SetDataDirectory Null Throws", () =>
            {
                ArmadaSettings settings = new ArmadaSettings();
                AssertThrows<ArgumentNullException>(() => settings.DataDirectory = null!);
            });

            await RunTest("ArmadaSettings SetDatabasePath Empty Throws", () =>
            {
                ArmadaSettings settings = new ArmadaSettings();
                AssertThrows<ArgumentNullException>(() => settings.DatabasePath = "");
            });

            await RunTest("ArmadaSettings SaveAndLoad RoundTrip", async () =>
            {
                string tempFile = Path.Combine(Path.GetTempPath(), "armada_test_settings_" + Guid.NewGuid().ToString("N") + ".json");

                try
                {
                    ArmadaSettings original = new ArmadaSettings();
                    original.AdmiralPort = 9000;
                    original.McpPort = 9001;
                    original.HeartbeatIntervalSeconds = 60;
                    original.DataRetentionDays = 90;
                    original.ApiKey = "test-key-123";

                    await original.SaveAsync(tempFile);

                    ArmadaSettings loaded = await ArmadaSettings.LoadAsync(tempFile);
                    AssertEqual(9000, loaded.AdmiralPort);
                    AssertEqual(9001, loaded.McpPort);
                    AssertEqual(60, loaded.HeartbeatIntervalSeconds);
                    AssertEqual(90, loaded.DataRetentionDays);
                    AssertEqual("test-key-123", loaded.ApiKey);
                }
                finally
                {
                    if (File.Exists(tempFile)) File.Delete(tempFile);
                }
            });

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

            await RunTest("ArmadaSettings LoadAsync NonExistentFile ReturnsDefaults", async () =>
            {
                ArmadaSettings settings = await ArmadaSettings.LoadAsync("/nonexistent/path/settings.json");
                AssertEqual(Constants.DefaultAdmiralPort, settings.AdmiralPort);
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

            await RunTest("ArmadaSettings InitializeDirectories NormalizesRelativeSqliteFilename", () =>
            {
                string tempDir = Path.Combine(Path.GetTempPath(), "armada_settings_db_" + Guid.NewGuid().ToString("N"));

                try
                {
                    ArmadaSettings settings = new ArmadaSettings();
                    settings.DataDirectory = tempDir;
                    settings.Database = new DatabaseSettings();
                    settings.Database.Filename = "armada.db";

                    settings.InitializeDirectories();

                    string expectedPath = Path.GetFullPath(Path.Combine(tempDir, "armada.db"));
                    AssertEqual(expectedPath, settings.Database.Filename, "Database.Filename");
                    AssertEqual(expectedPath, settings.DatabasePath, "DatabasePath");
                    AssertTrue(Directory.Exists(tempDir), "Data directory should exist");
                }
                finally
                {
                    if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
                }
            });

            await RunTest("ArmadaSettings LoadAsync LegacyDatabasePath SyncsSqliteFilename", async () =>
            {
                string tempDir = Path.Combine(Path.GetTempPath(), "armada_settings_legacy_" + Guid.NewGuid().ToString("N"));
                string tempFile = Path.Combine(Path.GetTempPath(), "armada_test_settings_legacy_" + Guid.NewGuid().ToString("N") + ".json");
                string legacyDbPath = Path.Combine(tempDir, "legacy.db");

                try
                {
                    string json = "{" +
                        "\"dataDirectory\":\"" + tempDir.Replace("\\", "\\\\") + "\"," +
                        "\"databasePath\":\"" + legacyDbPath.Replace("\\", "\\\\") + "\"," +
                        "\"database\":{\"type\":\"Sqlite\"}" +
                        "}";
                    await File.WriteAllTextAsync(tempFile, json).ConfigureAwait(false);

                    ArmadaSettings loaded = await ArmadaSettings.LoadAsync(tempFile);
                    AssertEqual(legacyDbPath, loaded.DatabasePath, "DatabasePath");
                    AssertEqual(legacyDbPath, loaded.Database.Filename, "Database.Filename");
                }
                finally
                {
                    if (File.Exists(tempFile)) File.Delete(tempFile);
                    if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
                }
            });

            await RunTest("ArmadaSettings NewSettings HaveCorrectDefaults", () =>
            {
                ArmadaSettings settings = new ArmadaSettings();
                AssertNull(settings.DefaultRuntime);
                AssertTrue(settings.Notifications);
                AssertTrue(settings.TerminalBell);
                AssertEqual(Constants.DefaultIdleCaptainTimeoutSeconds, settings.IdleCaptainTimeoutSeconds);
                AssertNotNull(settings.RemoteControl);
                AssertFalse(settings.RemoteControl.Enabled);
                AssertEqual(Constants.DefaultRemoteConnectTimeoutSeconds, settings.RemoteControl.ConnectTimeoutSeconds);
                AssertEqual(Constants.DefaultRemoteHeartbeatIntervalSeconds, settings.RemoteControl.HeartbeatIntervalSeconds);
                AssertEqual(Constants.DefaultRemoteTunnelPassword, settings.RemoteControl.Password);
            });

            await RunTest("ArmadaSettings IdleCaptainTimeoutSeconds NegativeThrows", () =>
            {
                ArmadaSettings settings = new ArmadaSettings();
                AssertThrows<ArgumentOutOfRangeException>(() => settings.IdleCaptainTimeoutSeconds = -1);
            });

            await RunTest("ArmadaSettings IdleCaptainTimeoutSeconds ZeroIsValid", () =>
            {
                ArmadaSettings settings = new ArmadaSettings();
                settings.IdleCaptainTimeoutSeconds = 0;
                AssertEqual(0, settings.IdleCaptainTimeoutSeconds);
            });

            await RunTest("ArmadaSettings IdleCaptainTimeoutSeconds PositiveIsValid", () =>
            {
                ArmadaSettings settings = new ArmadaSettings();
                settings.IdleCaptainTimeoutSeconds = 300;
                AssertEqual(300, settings.IdleCaptainTimeoutSeconds);
            });

            await RunTest("ArmadaSettings MessageTemplates DefaultsAreCorrect", () =>
            {
                ArmadaSettings settings = new ArmadaSettings();
                AssertNotNull(settings.MessageTemplates);
                AssertTrue(settings.MessageTemplates.EnableCommitMetadata);
                AssertTrue(settings.MessageTemplates.EnablePrMetadata);
                AssertContains("Armada-Mission-Id", settings.MessageTemplates.CommitMessageTemplate);
                AssertContains("Armada", settings.MessageTemplates.PrDescriptionTemplate);
                AssertContains("Merge armada mission", settings.MessageTemplates.MergeCommitTemplate);
            });

            await RunTest("ArmadaSettings NewSettings RoundTripSaveLoad", async () =>
            {
                string tempFile = Path.Combine(Path.GetTempPath(), "armada_test_settings_new_" + Guid.NewGuid().ToString("N") + ".json");

                try
                {
                    ArmadaSettings original = new ArmadaSettings();
                    original.DefaultRuntime = "ClaudeCode";
                    original.Notifications = false;
                    original.TerminalBell = false;
                    original.IdleCaptainTimeoutSeconds = 120;

                    await original.SaveAsync(tempFile);

                    ArmadaSettings loaded = await ArmadaSettings.LoadAsync(tempFile);
                    AssertEqual("ClaudeCode", loaded.DefaultRuntime);
                    AssertFalse(loaded.Notifications);
                    AssertFalse(loaded.TerminalBell);
                    AssertEqual(120, loaded.IdleCaptainTimeoutSeconds);
                }
                finally
                {
                    if (File.Exists(tempFile)) File.Delete(tempFile);
                }
            });

            await RunTest("ArmadaSettings MessageTemplates RoundTripSaveLoad", async () =>
            {
                string tempFile = Path.Combine(Path.GetTempPath(), "armada_test_settings_templates_" + Guid.NewGuid().ToString("N") + ".json");

                try
                {
                    ArmadaSettings original = new ArmadaSettings();
                    original.MessageTemplates.EnableCommitMetadata = false;
                    original.MessageTemplates.EnablePrMetadata = false;
                    original.MessageTemplates.CommitMessageTemplate = "Custom: {MissionId}";
                    original.MessageTemplates.PrDescriptionTemplate = "PR: {MissionId}";
                    original.MessageTemplates.MergeCommitTemplate = "Merge: {BranchName}";

                    await original.SaveAsync(tempFile);

                    ArmadaSettings loaded = await ArmadaSettings.LoadAsync(tempFile);
                    AssertNotNull(loaded.MessageTemplates);
                    AssertFalse(loaded.MessageTemplates.EnableCommitMetadata);
                    AssertFalse(loaded.MessageTemplates.EnablePrMetadata);
                    AssertEqual("Custom: {MissionId}", loaded.MessageTemplates.CommitMessageTemplate);
                    AssertEqual("PR: {MissionId}", loaded.MessageTemplates.PrDescriptionTemplate);
                    AssertEqual("Merge: {BranchName}", loaded.MessageTemplates.MergeCommitTemplate);
                }
                finally
                {
                    if (File.Exists(tempFile)) File.Delete(tempFile);
                }
            });

            await RunTest("ArmadaSettings RemoteControl RoundTripSaveLoad", async () =>
            {
                string tempFile = Path.Combine(Path.GetTempPath(), "armada_test_settings_remote_" + Guid.NewGuid().ToString("N") + ".json");

                try
                {
                    ArmadaSettings original = new ArmadaSettings();
                    original.RemoteControl.Enabled = true;
                    original.RemoteControl.TunnelUrl = "https://control.example.com/tunnel";
                    original.RemoteControl.InstanceId = "armada-test-instance";
                    original.RemoteControl.EnrollmentToken = "token-123";
                    original.RemoteControl.Password = "proxy-secret";
                    original.RemoteControl.ConnectTimeoutSeconds = 25;
                    original.RemoteControl.HeartbeatIntervalSeconds = 45;
                    original.RemoteControl.ReconnectBaseDelaySeconds = 8;
                    original.RemoteControl.ReconnectMaxDelaySeconds = 120;
                    original.RemoteControl.AllowInvalidCertificates = true;

                    await original.SaveAsync(tempFile);

                    ArmadaSettings loaded = await ArmadaSettings.LoadAsync(tempFile);
                    AssertTrue(loaded.RemoteControl.Enabled);
                    AssertEqual("https://control.example.com/tunnel", loaded.RemoteControl.TunnelUrl);
                    AssertEqual("armada-test-instance", loaded.RemoteControl.InstanceId);
                    AssertEqual("token-123", loaded.RemoteControl.EnrollmentToken);
                    AssertEqual("proxy-secret", loaded.RemoteControl.Password);
                    AssertEqual(25, loaded.RemoteControl.ConnectTimeoutSeconds);
                    AssertEqual(45, loaded.RemoteControl.HeartbeatIntervalSeconds);
                    AssertEqual(8, loaded.RemoteControl.ReconnectBaseDelaySeconds);
                    AssertEqual(120, loaded.RemoteControl.ReconnectMaxDelaySeconds);
                    AssertTrue(loaded.RemoteControl.AllowInvalidCertificates);
                }
                finally
                {
                    if (File.Exists(tempFile)) File.Delete(tempFile);
                }
            });
        }
    }
}
