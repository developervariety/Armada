namespace Armada.Test.Unit.Suites.Services
{
    using System.Threading.Tasks;
    using Armada.Core.Settings;
    using Armada.Test.Common;

    /// <summary>
    /// Unit coverage for CdWebhookSettings configuration validation.
    /// </summary>
    public class CdWebhookSettingsTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "CD Webhook Settings";

        /// <inheritdoc />
        protected override Task RunTestsAsync()
        {
            RunTest("IsConfigured_DisabledByDefault_ReturnsFalse", () =>
            {
                CdWebhookSettings settings = new CdWebhookSettings();

                AssertFalse(settings.IsConfigured());
                AssertFalse(settings.Enabled);
                return Task.CompletedTask;
            });

            RunTest("IsConfigured_EnabledWithoutUrl_ReturnsFalse", () =>
            {
                CdWebhookSettings settings = new CdWebhookSettings { Enabled = true, Url = null };

                AssertFalse(settings.IsConfigured());
                return Task.CompletedTask;
            });

            RunTest("IsConfigured_EnabledWithUrl_ReturnsTrue", () =>
            {
                CdWebhookSettings settings = new CdWebhookSettings { Enabled = true, Url = "https://cd.example.test/hook" };

                AssertTrue(settings.IsConfigured());
                return Task.CompletedTask;
            });

            RunTest("IsConfigured_BearerTokenOptional", () =>
            {
                CdWebhookSettings withToken = new CdWebhookSettings { Enabled = true, Url = "https://cd.example.test/hook", BearerToken = "t" };
                CdWebhookSettings withoutToken = new CdWebhookSettings { Enabled = true, Url = "https://cd.example.test/hook", BearerToken = null };

                AssertTrue(withToken.IsConfigured());
                AssertTrue(withoutToken.IsConfigured());
                return Task.CompletedTask;
            });

            return Task.CompletedTask;
        }
    }
}
