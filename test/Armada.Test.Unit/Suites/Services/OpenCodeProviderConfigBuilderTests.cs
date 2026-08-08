namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Threading.Tasks;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using Armada.Test.Common;

    /// <summary>
    /// Tests the credential-free inline OpenCode configuration used for external-provider
    /// captains (for example cun-ai).
    /// </summary>
    public class OpenCodeProviderConfigBuilderTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "OpenCode Provider Config Builder";

        private static ModelProvidersSettings RegistryWithCunAi()
        {
            ModelProvidersSettings registry = new ModelProvidersSettings();
            registry.Providers["cun-ai"] = new ModelProviderSettings
            {
                Name = "cun-ai",
                OpenAiBaseUrl = "https://cun.ai/v1",
                ApiKeyEnv = "CUN_AI_KEY"
            };
            return registry;
        }

        /// <summary>Runs the suite.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("IsProviderModel_RecognizesRegisteredPrefixes", () =>
            {
                AssertTrue(OpenCodeProviderConfigBuilder.IsProviderModel("cun-ai/claude-fable-5", RegistryWithCunAi()),
                    "A registered provider prefix must be recognized");
                AssertTrue(OpenCodeProviderConfigBuilder.IsProviderModel("  CUN-AI/claude-fable-5  ", RegistryWithCunAi()),
                    "Prefix matching must ignore case and surrounding whitespace");
                AssertFalse(OpenCodeProviderConfigBuilder.IsProviderModel("openai/gpt-5.6-sol", RegistryWithCunAi()),
                    "Other providers must not receive the overlay");
                AssertFalse(OpenCodeProviderConfigBuilder.IsProviderModel(null, RegistryWithCunAi()),
                    "Missing models must not receive the overlay");
                AssertFalse(OpenCodeProviderConfigBuilder.IsProviderModel("gpt-5.6-sol", RegistryWithCunAi()),
                    "A model without a provider prefix must not receive the overlay");
                return Task.CompletedTask;
            });

            await RunTest("IsProviderModel_UnregisteredPrefix_NotRecognizedWithEmptyDefaults", () =>
            {
                AssertFalse(OpenCodeProviderConfigBuilder.IsProviderModel("cun-ai/claude-fable-5", null),
                    "An unregistered provider must not be recognized with the empty default registry");
                return Task.CompletedTask;
            });

            await RunTest("Build_EmitsProviderOverlay", () =>
            {
                string json = OpenCodeProviderConfigBuilder.Build("cun-ai/claude-fable-5", null, null, RegistryWithCunAi());

                AssertContains("\"npm\": \"@ai-sdk/openai-compatible\"", json, "The provider must use the OpenAI-compatible AI SDK adapter");
                AssertContains("\"baseURL\": \"https://cun.ai/v1\"", json, "The provider's OpenAI-compatible endpoint must be used");
                AssertContains("\"apiKey\": \"{env:CUN_AI_KEY}\"", json, "The configuration must reference the environment key without embedding a credential");
                AssertContains("\"id\": \"claude-fable-5\"", json, "The provider-facing id must omit the Armada namespace prefix");

                AssertFalse(json.Contains("sk-", StringComparison.OrdinalIgnoreCase), "The generated configuration must never contain a provider secret");
                return Task.CompletedTask;
            });

            await RunTest("Build_TrimsSurroundingWhitespace", () =>
            {
                string json = OpenCodeProviderConfigBuilder.Build("  cun-ai/claude-fable-5  ", null, null, RegistryWithCunAi());

                AssertContains("\"id\": \"claude-fable-5\"", json, "The trimmed input must parse and emit the provider-facing id");
                AssertFalse(json.Contains("cun-ai/claude-fable-5", StringComparison.Ordinal),
                    "The Armada namespace prefix must not leak into the provider-facing id");
                return Task.CompletedTask;
            });

            await RunTest("Build_EmbedsCaptainKey", () =>
            {
                string json = OpenCodeProviderConfigBuilder.Build("cun-ai/claude-fable-5", "captain-key-not-a-real-credential", null, RegistryWithCunAi());

                AssertContains("\"apiKey\": \"captain-key-not-a-real-credential\"", json, "A per-captain key must be embedded in the overlay");
                AssertFalse(json.Contains("{env:CUN_AI_KEY}", StringComparison.Ordinal),
                    "A per-captain key must replace the environment reference");
                return Task.CompletedTask;
            });

            await RunTest("Build_EmbedsCaptainBaseUrl", () =>
            {
                string json = OpenCodeProviderConfigBuilder.Build("cun-ai/claude-fable-5", "captain-key-not-a-real-credential", "https://proxy.example.test/v1", RegistryWithCunAi());

                AssertContains("\"baseURL\": \"https://proxy.example.test/v1\"", json,
                    "A per-captain base URL must replace the provider default");
                return Task.CompletedTask;
            });

            await RunTest("Build_UsesEnvironmentKeyReferenceWhenCaptainKeyAbsent", () =>
            {
                string json = OpenCodeProviderConfigBuilder.Build("cun-ai/claude-fable-5", null, null, RegistryWithCunAi());

                AssertContains("\"apiKey\": \"{env:CUN_AI_KEY}\"", json,
                    "Without a captain key the overlay must reference the provider's environment variable");
                return Task.CompletedTask;
            });

            await RunTest("Build_RejectsUnregisteredModel", () =>
            {
                bool threw = false;
                try
                {
                    OpenCodeProviderConfigBuilder.Build("opencode-go/deepseek-v4-flash");
                }
                catch (ArgumentException)
                {
                    threw = true;
                }

                AssertTrue(threw, "An unregistered model must not produce a provider overlay");
                return Task.CompletedTask;
            });

            await RunTest("Build_RejectsProviderWithoutKeySource", () =>
            {
                // A registered provider without an ApiKeyEnv and no per-captain key cannot
                // authenticate the overlay, so the build must refuse rather than ship a
                // half-configured provider.
                ModelProvidersSettings registry = RegistryWithCunAi();
                registry.Providers["cun-ai"].ApiKeyEnv = String.Empty;

                bool threw = false;
                try
                {
                    OpenCodeProviderConfigBuilder.Build("cun-ai/claude-fable-5", null, null, registry);
                }
                catch (ArgumentException)
                {
                    threw = true;
                }

                AssertTrue(threw, "A provider without any key source must be rejected");
                return Task.CompletedTask;
            });
        }
    }
}
