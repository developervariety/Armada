namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Linq;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Armada.Core.Services;
    using Armada.Test.Common;

    /// <summary>
    /// Tests the credential-free inline OpenCode configuration used for Zyloo captains.
    /// </summary>
    public class OpenCodeZylooProviderConfigBuilderTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "OpenCode Zyloo Provider Config Builder";

        /// <summary>Runs the suite.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("IsZylooModel_RecognizesCanonicalPrefix", () =>
            {
                AssertTrue(OpenCodeZylooProviderConfigBuilder.IsZylooModel("zyloo/gpt-5.5"), "Canonical lower-case Zyloo IDs must be recognized");
                AssertTrue(OpenCodeZylooProviderConfigBuilder.IsZylooModel("  ZYLOO/claude-opus-4-7  "), "Prefix matching must ignore case and surrounding whitespace");
                AssertFalse(OpenCodeZylooProviderConfigBuilder.IsZylooModel("openai/gpt-5.5"), "Other providers must not receive the Zyloo overlay");
                AssertFalse(OpenCodeZylooProviderConfigBuilder.IsZylooModel(null), "Missing models must not receive the Zyloo overlay");
                return Task.CompletedTask;
            });

            await RunTest("Build_EmitsExpectedProviderWithoutSecret", () =>
            {
                string json = OpenCodeZylooProviderConfigBuilder.Build("zyloo/claude-opus-4-7");

                AssertContains("\"npm\": \"@ai-sdk/openai-compatible\"", json, "Zyloo must use the OpenAI-compatible AI SDK adapter");
                AssertContains("\"baseURL\": \"https://api.zyloo.io/v1\"", json, "Zyloo must use the documented OpenAI-compatible endpoint");
                AssertContains("\"apiKey\": \"{env:ZYLOO_KEY}\"", json, "The configuration must reference the environment key without embedding a credential");
                AssertContains("\"zyloo/claude-opus-4-7\": {}", json, "Only the requested model should be registered");
                AssertFalse(json.Contains("sk-zy-", StringComparison.OrdinalIgnoreCase), "The generated configuration must never contain a Zyloo secret");
                AssertFalse(json.Contains("\r\n"), "The generated configuration must use LF line endings");
                return Task.CompletedTask;
            });

            await RunTest("Build_ProducesValidJsonWithRequestedModel", () =>
            {
                string json = OpenCodeZylooProviderConfigBuilder.Build("  zyloo/gpt-5.5  ");
                using JsonDocument document = JsonDocument.Parse(json);
                string model = document.RootElement
                    .GetProperty("provider")
                    .GetProperty("zyloo")
                    .GetProperty("models")
                    .EnumerateObject()
                    .First()
                    .Name;

                AssertEqual("zyloo/gpt-5.5", model, "The registered model must be trimmed and retain its canonical Zyloo identifier");
                return Task.CompletedTask;
            });

            await RunTest("Build_RejectsNonZylooModel", () =>
            {
                bool threw = false;
                try
                {
                    OpenCodeZylooProviderConfigBuilder.Build("opencode/deepseek-v4-flash-free");
                }
                catch (ArgumentException)
                {
                    threw = true;
                }

                AssertTrue(threw, "A non-Zyloo model must not produce a Zyloo provider overlay");
                return Task.CompletedTask;
            });
        }
    }
}
