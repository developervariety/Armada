namespace Armada.Test.Unit.Suites.Services
{
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
        }
    }
}
