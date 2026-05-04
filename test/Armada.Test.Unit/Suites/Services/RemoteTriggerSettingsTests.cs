namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.IO;
    using System.Linq;
    using System.Reflection;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Armada.Core.Settings;
    using Armada.Test.Common;

    public class RemoteTriggerSettingsTests : TestSuite
    {
        public override string Name => "RemoteTrigger Settings";

        protected override async Task RunTestsAsync()
        {
            await RunTest("IsDrainerConfigured_AllFieldsSet_True", () =>
            {
                RemoteTriggerSettings s = new RemoteTriggerSettings
                {
                    Enabled = true,
                    DrainerFireUrl = "https://api.anthropic.com/v1/claude_code/routines/trig_x/fire",
                    DrainerBearerToken = "sk-ant-oat01-xxxxx",
                };
                AssertTrue(s.IsDrainerConfigured());
                return Task.CompletedTask;
            });

            await RunTest("IsDrainerConfigured_DisabledFlag_False", () =>
            {
                RemoteTriggerSettings s = new RemoteTriggerSettings
                {
                    Enabled = false,
                    DrainerFireUrl = "https://api.anthropic.com/v1/claude_code/routines/trig_x/fire",
                    DrainerBearerToken = "sk-ant-oat01-xxxxx",
                };
                AssertFalse(s.IsDrainerConfigured());
                return Task.CompletedTask;
            });

            await RunTest("IsDrainerConfigured_MissingUrl_False", () =>
            {
                RemoteTriggerSettings s = new RemoteTriggerSettings { Enabled = true, DrainerBearerToken = "x" };
                AssertFalse(s.IsDrainerConfigured());
                return Task.CompletedTask;
            });

            await RunTest("IsCriticalConfigured_NoCriticalUrl_False", () =>
            {
                RemoteTriggerSettings s = new RemoteTriggerSettings
                {
                    Enabled = true,
                    DrainerFireUrl = "x",
                    DrainerBearerToken = "x",
                };
                AssertFalse(s.IsCriticalConfigured());
                AssertTrue(s.IsDrainerConfigured(), "Drainer config independent of critical config");
                return Task.CompletedTask;
            });

            await RunTest("IsDrainerConfigured_ModeDisabledWithRemoteFireFields_False", () =>
            {
                RemoteTriggerSettings s = new RemoteTriggerSettings
                {
                    Enabled = true,
                    Mode = RemoteTriggerMode.Disabled,
                    DrainerFireUrl = "https://api.anthropic.com/v1/claude_code/routines/trig_x/fire",
                    DrainerBearerToken = "sk-ant-oat01-xxxxx",
                };
                AssertFalse(s.IsDrainerConfigured(), "Disabled mode should suppress drainer config even when RemoteFire fields are present");
                return Task.CompletedTask;
            });

            await RunTest("IsCriticalConfigured_ModeDisabledWithRemoteFireFields_False", () =>
            {
                RemoteTriggerSettings s = new RemoteTriggerSettings
                {
                    Enabled = true,
                    Mode = RemoteTriggerMode.Disabled,
                    CriticalFireUrl = "https://api.anthropic.com/v1/claude_code/routines/trig_critical/fire",
                    CriticalBearerToken = "sk-ant-oat01-critical",
                };
                AssertFalse(s.IsCriticalConfigured(), "Disabled mode should suppress critical config even when RemoteFire fields are present");
                return Task.CompletedTask;
            });

            await RunTest("RemoteTriggerMode_DoesNotContain_LocalDaemon", () =>
            {
                string[] names = Enum.GetNames(typeof(RemoteTriggerMode));
                AssertFalse(names.Contains("LocalDaemon"), "LocalDaemon must not be a member of RemoteTriggerMode");
                return Task.CompletedTask;
            });

            await RunTest("RemoteTriggerSettings_HasNo_LocalDaemonProperty", () =>
            {
                PropertyInfo? prop = typeof(RemoteTriggerSettings).GetProperty("LocalDaemon");
                AssertNull(prop, "RemoteTriggerSettings must not expose a LocalDaemon property");
                return Task.CompletedTask;
            });

            await RunTest("RemoteTriggerSettings_HasNo_IsLocalDaemonConfiguredMethod", () =>
            {
                MethodInfo? method = typeof(RemoteTriggerSettings).GetMethod("IsLocalDaemonConfigured");
                AssertNull(method, "RemoteTriggerSettings must not expose IsLocalDaemonConfigured");
                return Task.CompletedTask;
            });

            await RunTest("LocalDaemonSettings_TypeDoesNotExist_InArmadaCore", () =>
            {
                Assembly coreAssembly = typeof(RemoteTriggerSettings).Assembly;
                Type? localDaemonType = coreAssembly.GetType("Armada.Core.Settings.LocalDaemonSettings");
                AssertNull(localDaemonType, "LocalDaemonSettings type must not exist in Armada.Core");
                return Task.CompletedTask;
            });

            await RunTest("RemoteTriggerMode_ContainsAgentWake", () =>
            {
                string[] names = Enum.GetNames(typeof(RemoteTriggerMode));
                AssertTrue(names.Contains("AgentWake"), "AgentWake must be a member of RemoteTriggerMode");
                return Task.CompletedTask;
            });

            await RunTest("AgentWakeRuntime_ContainsAuto", () =>
            {
                string[] names = Enum.GetNames(typeof(AgentWakeRuntime));
                AssertTrue(names.Contains("Auto"), "AgentWakeRuntime must support Auto mode");
                AgentWakeSettings settings = new AgentWakeSettings { Runtime = AgentWakeRuntime.Auto };
                AssertEqual("codex", settings.GetEffectiveCommand(), "Auto should default command resolution to the first fallback runtime");
                return Task.CompletedTask;
            });

            await RunTest("IsAgentWakeConfigured_EnabledAndAgentWakeMode_True", () =>
            {
                RemoteTriggerSettings s = new RemoteTriggerSettings
                {
                    Enabled = true,
                    Mode = RemoteTriggerMode.AgentWake,
                };
                AssertTrue(s.IsAgentWakeConfigured(), "Enabled=true and Mode=AgentWake with defaults should be configured");
                return Task.CompletedTask;
            });

            await RunTest("IsAgentWakeConfigured_EnabledFalse_False", () =>
            {
                RemoteTriggerSettings s = new RemoteTriggerSettings
                {
                    Enabled = false,
                    Mode = RemoteTriggerMode.AgentWake,
                };
                AssertFalse(s.IsAgentWakeConfigured(), "Enabled=false should make IsAgentWakeConfigured return false");
                return Task.CompletedTask;
            });

            await RunTest("IsAgentWakeConfigured_RemoteFireMode_False", () =>
            {
                RemoteTriggerSettings s = new RemoteTriggerSettings
                {
                    Enabled = true,
                    Mode = RemoteTriggerMode.RemoteFire,
                    DrainerFireUrl = "https://api.anthropic.com/v1/claude_code/routines/trig_x/fire",
                    DrainerBearerToken = "sk-ant-oat01-xxxxx",
                };
                AssertFalse(s.IsAgentWakeConfigured(), "RemoteFire mode should not satisfy IsAgentWakeConfigured");
                return Task.CompletedTask;
            });

            await RunTest("IsAgentWakeConfigured_DisabledMode_False", () =>
            {
                RemoteTriggerSettings s = new RemoteTriggerSettings
                {
                    Enabled = true,
                    Mode = RemoteTriggerMode.Disabled,
                };
                AssertFalse(s.IsAgentWakeConfigured(), "Disabled mode should not satisfy IsAgentWakeConfigured");
                return Task.CompletedTask;
            });

            await RunTest("AgentWakeSettings_DefaultCommand_IsClaudeForClaudeRuntime", () =>
            {
                AgentWakeSettings aws = new AgentWakeSettings { Runtime = AgentWakeRuntime.Claude };
                AssertEqual("claude", aws.GetEffectiveCommand(), "default Claude command should be 'claude'");
                return Task.CompletedTask;
            });

            await RunTest("AgentWakeSettings_DefaultCommand_IsCodexForCodexRuntime", () =>
            {
                AgentWakeSettings aws = new AgentWakeSettings { Runtime = AgentWakeRuntime.Codex };
                AssertEqual("codex", aws.GetEffectiveCommand(), "default Codex command should be 'codex'");
                return Task.CompletedTask;
            });

            await RunTest("AgentWakeSettings_CustomCommand_OverridesDefault", () =>
            {
                AgentWakeSettings aws = new AgentWakeSettings { Runtime = AgentWakeRuntime.Claude, Command = "/usr/local/bin/claude" };
                AssertEqual("/usr/local/bin/claude", aws.GetEffectiveCommand(), "explicit Command should override default");
                return Task.CompletedTask;
            });

            await RunTest("LoadAsync_RemoteTriggerMode_StringAgentWake_Deserializes", async () =>
            {
                string tempFile = Path.Combine(Path.GetTempPath(), "armada_test_rts_" + Guid.NewGuid().ToString("N") + ".json");
                try
                {
                    string json = "{\"remoteTrigger\":{\"enabled\":true,\"mode\":\"AgentWake\"}}";
                    await File.WriteAllTextAsync(tempFile, json).ConfigureAwait(false);
                    ArmadaSettings loaded = await ArmadaSettings.LoadAsync(tempFile);
                    AssertNotNull(loaded.RemoteTrigger, "RemoteTrigger section should be present");
                    AssertTrue(loaded.RemoteTrigger!.Enabled, "Enabled should be true");
                    AssertEqual(RemoteTriggerMode.AgentWake, loaded.RemoteTrigger.Mode, "Mode should deserialize from string \"AgentWake\"");
                }
                finally
                {
                    if (File.Exists(tempFile)) File.Delete(tempFile);
                }
            });

            await RunTest("LoadAsync_AgentWakeRuntime_StringCodex_Deserializes", async () =>
            {
                string tempFile = Path.Combine(Path.GetTempPath(), "armada_test_rts_" + Guid.NewGuid().ToString("N") + ".json");
                try
                {
                    string json = "{\"remoteTrigger\":{\"enabled\":true,\"mode\":\"AgentWake\",\"agentWake\":{\"runtime\":\"Codex\"}}}";
                    await File.WriteAllTextAsync(tempFile, json).ConfigureAwait(false);
                    ArmadaSettings loaded = await ArmadaSettings.LoadAsync(tempFile);
                    AssertNotNull(loaded.RemoteTrigger, "RemoteTrigger section should be present");
                    AssertNotNull(loaded.RemoteTrigger!.AgentWake, "AgentWake section should be present");
                    AssertEqual(AgentWakeRuntime.Codex, loaded.RemoteTrigger.AgentWake!.Runtime, "Runtime should deserialize from string \"Codex\"");
                }
                finally
                {
                    if (File.Exists(tempFile)) File.Delete(tempFile);
                }
            });

            await RunTest("LoadAsync_AgentWakeRuntime_StringAuto_Deserializes", async () =>
            {
                string tempFile = Path.Combine(Path.GetTempPath(), "armada_test_rts_" + Guid.NewGuid().ToString("N") + ".json");
                try
                {
                    string json = "{\"remoteTrigger\":{\"enabled\":true,\"mode\":\"AgentWake\",\"agentWake\":{\"runtime\":\"Auto\",\"runtimePreference\":[\"Codex\",\"Claude\"]}}}";
                    await File.WriteAllTextAsync(tempFile, json).ConfigureAwait(false);
                    ArmadaSettings loaded = await ArmadaSettings.LoadAsync(tempFile);
                    AssertNotNull(loaded.RemoteTrigger, "RemoteTrigger section should be present");
                    AssertNotNull(loaded.RemoteTrigger!.AgentWake, "AgentWake section should be present");
                    AssertEqual(AgentWakeRuntime.Auto, loaded.RemoteTrigger.AgentWake!.Runtime, "Runtime should deserialize from string \"Auto\"");
                    AssertEqual(AgentWakeRuntime.Codex, loaded.RemoteTrigger.AgentWake.RuntimePreference![0], "RuntimePreference[0]");
                    AssertEqual(AgentWakeRuntime.Claude, loaded.RemoteTrigger.AgentWake.RuntimePreference![1], "RuntimePreference[1]");
                }
                finally
                {
                    if (File.Exists(tempFile)) File.Delete(tempFile);
                }
            });

            await RunTest("LoadAsync_RemoteTrigger_DisabledWithStringMode_DoesNotThrow", async () =>
            {
                string tempFile = Path.Combine(Path.GetTempPath(), "armada_test_rts_" + Guid.NewGuid().ToString("N") + ".json");
                try
                {
                    string json = "{\"remoteTrigger\":{\"enabled\":false,\"mode\":\"RemoteFire\"}}";
                    await File.WriteAllTextAsync(tempFile, json).ConfigureAwait(false);
                    ArmadaSettings loaded = await ArmadaSettings.LoadAsync(tempFile);
                    AssertNotNull(loaded.RemoteTrigger, "RemoteTrigger section should be present");
                    AssertFalse(loaded.RemoteTrigger!.Enabled, "Enabled should be false");
                    AssertEqual(RemoteTriggerMode.RemoteFire, loaded.RemoteTrigger.Mode, "Mode should deserialize from string \"RemoteFire\"");
                }
                finally
                {
                    if (File.Exists(tempFile)) File.Delete(tempFile);
                }
            });

            await RunTest("LoadAsync_RemoteTriggerMode_StringLocalDaemon_Throws", async () =>
            {
                string tempFile = Path.Combine(Path.GetTempPath(), "armada_test_rts_" + Guid.NewGuid().ToString("N") + ".json");
                try
                {
                    string json = "{\"remoteTrigger\":{\"enabled\":true,\"mode\":\"LocalDaemon\"}}";
                    await File.WriteAllTextAsync(tempFile, json).ConfigureAwait(false);
                    await AssertThrowsAsync<JsonException>(() => ArmadaSettings.LoadAsync(tempFile));
                }
                finally
                {
                    if (File.Exists(tempFile)) File.Delete(tempFile);
                }
            });

            await RunTest("SaveAsync_RemoteTrigger_AgentWakeMode_WritesStringValue", async () =>
            {
                string tempFile = Path.Combine(Path.GetTempPath(), "armada_test_rts_" + Guid.NewGuid().ToString("N") + ".json");
                try
                {
                    ArmadaSettings original = new ArmadaSettings();
                    original.RemoteTrigger = new RemoteTriggerSettings
                    {
                        Enabled = true,
                        Mode = RemoteTriggerMode.AgentWake,
                        AgentWake = new AgentWakeSettings { Runtime = AgentWakeRuntime.Codex }
                    };
                    await original.SaveAsync(tempFile);

                    string written = await File.ReadAllTextAsync(tempFile).ConfigureAwait(false);
                    AssertContains("AgentWake", written, "Saved JSON should contain string \"AgentWake\" for Mode");
                    AssertContains("Codex", written, "Saved JSON should contain string \"Codex\" for Runtime");

                    ArmadaSettings loaded = await ArmadaSettings.LoadAsync(tempFile);
                    AssertNotNull(loaded.RemoteTrigger, "RemoteTrigger should round-trip");
                    AssertEqual(RemoteTriggerMode.AgentWake, loaded.RemoteTrigger!.Mode, "Mode round-trip");
                    AssertNotNull(loaded.RemoteTrigger.AgentWake, "AgentWake should round-trip");
                    AssertEqual(AgentWakeRuntime.Codex, loaded.RemoteTrigger.AgentWake!.Runtime, "Runtime round-trip");
                }
                finally
                {
                    if (File.Exists(tempFile)) File.Delete(tempFile);
                }
            });
        }
    }
}
