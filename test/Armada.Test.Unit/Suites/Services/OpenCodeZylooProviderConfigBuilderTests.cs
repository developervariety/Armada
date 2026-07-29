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
                AssertContains("\"claude-opus-4-7\": {", json, "The provider-local model ID should be registered");
                AssertContains("\"id\": \"zyloo/claude-opus-4-7\"", json, "The local alias must map to the canonical Zyloo API model ID");
                AssertFalse(json.Contains("\"zyloo/claude-opus-4-7\": {", StringComparison.Ordinal),
                    "The provider model map must not repeat the provider prefix");
                AssertFalse(json.Contains("sk-zy-", StringComparison.OrdinalIgnoreCase), "The generated configuration must never contain a Zyloo secret");
                AssertFalse(json.Contains("\r\n"), "The generated configuration must use LF line endings");
                return Task.CompletedTask;
            });

            await RunTest("Build_ProducesValidJsonWithRequestedModel", () =>
            {
                string json = OpenCodeZylooProviderConfigBuilder.Build("  zyloo/gpt-5.5  ");
                using JsonDocument document = JsonDocument.Parse(json);
                JsonProperty model = document.RootElement
                    .GetProperty("provider")
                    .GetProperty("zyloo")
                    .GetProperty("models")
                    .EnumerateObject()
                    .First();

                AssertEqual("gpt-5.5", model.Name, "The registered model must be provider-local and trimmed");
                AssertEqual("zyloo/gpt-5.5", model.Value.GetProperty("id").GetString(),
                    "The provider-local alias must preserve the canonical upstream model ID");
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
