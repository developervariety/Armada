namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Linq;
    using System.Reflection;
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
        }
    }
}
