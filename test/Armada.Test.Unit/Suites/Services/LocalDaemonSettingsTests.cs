namespace Armada.Test.Unit.Suites.Services
{
    using System.Threading.Tasks;
    using Armada.Core.Settings;
    using Armada.Test.Common;

    public class LocalDaemonSettingsTests : TestSuite
    {
        public override string Name => "LocalDaemon Settings";

        protected override async Task RunTestsAsync()
        {
            await RunTest("IsLocalDaemonConfigured_NullLocalDaemon_ReturnsFalse", () =>
            {
                RemoteTriggerSettings s = new RemoteTriggerSettings
                {
                    Enabled = true,
                    Mode = RemoteTriggerMode.LocalDaemon,
                    LocalDaemon = null,
                };
                AssertFalse(s.IsLocalDaemonConfigured(), "null LocalDaemon block should return false");
                return Task.CompletedTask;
            });

            await RunTest("IsLocalDaemonConfigured_EmptyCommand_ReturnsFalse", () =>
            {
                RemoteTriggerSettings s = new RemoteTriggerSettings
                {
                    Enabled = true,
                    Mode = RemoteTriggerMode.LocalDaemon,
                    LocalDaemon = new LocalDaemonSettings { Command = "" },
                };
                AssertFalse(s.IsLocalDaemonConfigured(), "empty Command should return false");
                return Task.CompletedTask;
            });

            await RunTest("IsLocalDaemonConfigured_WhitespaceCommand_ReturnsFalse", () =>
            {
                RemoteTriggerSettings s = new RemoteTriggerSettings
                {
                    Enabled = true,
                    Mode = RemoteTriggerMode.LocalDaemon,
                    LocalDaemon = new LocalDaemonSettings { Command = "   " },
                };
                AssertFalse(s.IsLocalDaemonConfigured(), "whitespace-only Command should return false");
                return Task.CompletedTask;
            });

            await RunTest("IsLocalDaemonConfigured_ValidCommand_ReturnsTrue", () =>
            {
                RemoteTriggerSettings s = new RemoteTriggerSettings
                {
                    Enabled = true,
                    Mode = RemoteTriggerMode.LocalDaemon,
                    LocalDaemon = new LocalDaemonSettings { Command = "claude" },
                };
                AssertTrue(s.IsLocalDaemonConfigured(), "valid Command should return true");
                return Task.CompletedTask;
            });

            await RunTest("IsLocalDaemonConfigured_EnabledFalse_ReturnsFalse", () =>
            {
                RemoteTriggerSettings s = new RemoteTriggerSettings
                {
                    Enabled = false,
                    Mode = RemoteTriggerMode.LocalDaemon,
                    LocalDaemon = new LocalDaemonSettings { Command = "claude" },
                };
                AssertFalse(s.IsLocalDaemonConfigured(), "Enabled=false should return false even with valid Command");
                return Task.CompletedTask;
            });

            await RunTest("IsLocalDaemonConfigured_WrongMode_ReturnsFalse", () =>
            {
                RemoteTriggerSettings s = new RemoteTriggerSettings
                {
                    Enabled = true,
                    Mode = RemoteTriggerMode.RemoteFire,
                    LocalDaemon = new LocalDaemonSettings { Command = "claude" },
                };
                AssertFalse(s.IsLocalDaemonConfigured(), "RemoteFire mode should return false from IsLocalDaemonConfigured");
                return Task.CompletedTask;
            });

            await RunTest("IsDrainerConfigured_LocalDaemonMode_ReturnsFalse", () =>
            {
                RemoteTriggerSettings s = new RemoteTriggerSettings
                {
                    Enabled = true,
                    Mode = RemoteTriggerMode.LocalDaemon,
                    DrainerFireUrl = "https://api.anthropic.com/v1/claude_code/routines/trig_x/fire",
                    DrainerBearerToken = "sk-ant-test",
                };
                AssertFalse(s.IsDrainerConfigured(), "IsDrainerConfigured should return false when Mode is not RemoteFire");
                return Task.CompletedTask;
            });
        }
    }
}
