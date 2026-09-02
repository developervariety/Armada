namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Diagnostics;
    using System.Reflection;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using Armada.Runtimes;
    using Armada.Test.Common;

    /// <summary>
    /// Tests that a Claude captain served by an external provider is pointed at that
    /// provider's Anthropic-native endpoint, and that doing so cannot disturb a captain
    /// on the native Anthropic account.
    /// </summary>
    public class ClaudeCodeProviderRoutingTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Claude Code Provider Routing";

        private static void InvokeRouting(ProcessStartInfo startInfo, Captain? captain)
        {
            InvokeRouting(startInfo, captain, null);
        }

        private static void InvokeRouting(ProcessStartInfo startInfo, Captain? captain, ModelProvidersSettings? providers)
        {
            MethodInfo? method = typeof(ClaudeCodeRuntime).GetMethod(
                "ApplyProviderRouting",
                BindingFlags.Static | BindingFlags.NonPublic);
            if (method == null) throw new InvalidOperationException("Could not find ApplyProviderRouting.");

            MethodInfo? modelMethod = typeof(ClaudeCodeRuntime).GetMethod(
                "ApplyProviderModelRouting",
                BindingFlags.Static | BindingFlags.NonPublic);
            if (modelMethod == null) throw new InvalidOperationException("Could not find ApplyProviderModelRouting.");

            if (captain != null)
            {
                modelMethod.Invoke(null, new object?[] { startInfo, captain, captain.Model, providers });
            }
            else
            {
                method.Invoke(null, new object?[] { startInfo, captain });
            }
        }

        private static Captain CaptainWithModel(string name, string? model)
        {
            Captain captain = new Captain(name);
            captain.Model = model;
            return captain;
        }

        private static Captain CaptainWithCredential(string name, string? model, string? apiKey, string? apiBaseUrl = null)
        {
            Captain captain = CaptainWithModel(name, model);
            captain.ApiKey = apiKey;
            captain.ApiBaseUrl = apiBaseUrl;
            return captain;
        }

        private static ModelProvidersSettings RegistryWithExampleProvider()
        {
            ModelProvidersSettings registry = new ModelProvidersSettings();
            registry.Providers["example-provider"] = new ModelProviderSettings
            {
                Name = "example-provider",
                BaseUrl = "https://example-provider",
                ApiKeyEnv = "EXAMPLE_PROVIDER_KEY"
            };
            return registry;
        }

        /// <summary>Runs the suite.</summary>
        protected override async Task RunTestsAsync()
        {
            string? originalCun = Environment.GetEnvironmentVariable("EXAMPLE_PROVIDER_KEY");
            Environment.SetEnvironmentVariable("EXAMPLE_PROVIDER_KEY", "test-key-not-a-real-credential");

            try
            {
                await RunTest("ProviderModel_IsRoutedToTheProviderEndpoint", () =>
                {
                    ProcessStartInfo startInfo = new ProcessStartInfo();
                    InvokeRouting(startInfo, CaptainWithModel("example-provider-1", "example-provider/claude-fable-5"), RegistryWithExampleProvider());

                    AssertEqual("https://example-provider", startInfo.Environment["ANTHROPIC_BASE_URL"],
                        "A provider-prefixed captain must be pointed at the provider's Anthropic-native endpoint");
                    AssertEqual("test-key-not-a-real-credential", startInfo.Environment["ANTHROPIC_API_KEY"],
                        "The provider key must be supplied as ANTHROPIC_API_KEY, the form providers document");
                    return Task.CompletedTask;
                });

                await RunTest("NativeCaptain_IsLeftEntirelyAlone", () =>
                {
                    // The whole design rests on this: a native Claude captain launched beside an
                    // external-provider one must keep its own account and endpoint.
                    ProcessStartInfo startInfo = new ProcessStartInfo();
                    InvokeRouting(startInfo, CaptainWithModel("native-1", "claude-opus-4-8"));

                    AssertFalse(startInfo.Environment.ContainsKey("ANTHROPIC_BASE_URL"),
                        "A native Claude captain must never be redirected to another endpoint");
                    AssertFalse(startInfo.Environment.ContainsKey("ANTHROPIC_API_KEY"),
                        "A native Claude captain must never receive a provider credential");
                    return Task.CompletedTask;
                });

                await RunTest("ConcurrentLaunches_DoNotLeakBetweenCaptains", () =>
                {
                    // Two captains launched from the same admiral get independent environments.
                    ProcessStartInfo provider = new ProcessStartInfo();
                    ProcessStartInfo native = new ProcessStartInfo();

                    InvokeRouting(provider, CaptainWithModel("example-provider-2", "example-provider/claude-fable-5"), RegistryWithExampleProvider());
                    InvokeRouting(native, CaptainWithModel("native-2", "claude-fable-5"));

                    AssertTrue(provider.Environment.ContainsKey("ANTHROPIC_BASE_URL"),
                        "The provider captain keeps its redirect");
                    AssertFalse(native.Environment.ContainsKey("ANTHROPIC_BASE_URL"),
                        "The native captain launched alongside it must be unaffected");
                    return Task.CompletedTask;
                });

                await RunTest("InheritedAuthToken_IsClearedForRoutedCaptainsOnly", () =>
                {
                    // An inherited ANTHROPIC_AUTH_TOKEN outranks the API key inside the CLI, so it must
                    // be cleared for the routed child -- and left intact for everyone else.
                    ProcessStartInfo provider = new ProcessStartInfo();
                    provider.Environment["ANTHROPIC_AUTH_TOKEN"] = "inherited-native-token";
                    InvokeRouting(provider, CaptainWithModel("example-provider-3", "example-provider/claude-fable-5"), RegistryWithExampleProvider());
                    AssertFalse(provider.Environment.ContainsKey("ANTHROPIC_AUTH_TOKEN"),
                        "A stale inherited auth token must not override the provider API key");

                    ProcessStartInfo native = new ProcessStartInfo();
                    native.Environment["ANTHROPIC_AUTH_TOKEN"] = "inherited-native-token";
                    InvokeRouting(native, CaptainWithModel("native-3", "claude-opus-4-7"));
                    AssertEqual("inherited-native-token", native.Environment["ANTHROPIC_AUTH_TOKEN"],
                        "A native captain's own credential must be left untouched");
                    return Task.CompletedTask;
                });

                await RunTest("CaptainApiKey_WinsOverTheHostEnvironmentKey", () =>
                {
                    // Two subscriptions run side by side: a captain's own key must beat the
                    // host-level key that serves the other subscription.
                    ProcessStartInfo startInfo = new ProcessStartInfo();
                    InvokeRouting(startInfo, CaptainWithCredential("example-provider-keyed", "example-provider/claude-fable-5", "captain-key-not-a-real-credential"), RegistryWithExampleProvider());

                    AssertEqual("captain-key-not-a-real-credential", startInfo.Environment["ANTHROPIC_API_KEY"],
                        "The per-captain key must win over the environment fallback");
                    AssertEqual("https://example-provider", startInfo.Environment["ANTHROPIC_BASE_URL"],
                        "The per-captain key must still use the default provider endpoint");
                    return Task.CompletedTask;
                });

                await RunTest("CaptainApiKey_FallsBackToTheHostEnvironmentKey", () =>
                {
                    // A captain without its own key keeps the single-key behavior.
                    ProcessStartInfo startInfo = new ProcessStartInfo();
                    InvokeRouting(startInfo, CaptainWithCredential("example-provider-env", "example-provider/claude-fable-5", null), RegistryWithExampleProvider());

                    AssertEqual("test-key-not-a-real-credential", startInfo.Environment["ANTHROPIC_API_KEY"],
                        "The host-level key must remain the fallback for captains without a key");
                    return Task.CompletedTask;
                });

                await RunTest("CaptainApiBaseUrl_OverridesTheDefaultEndpoint", () =>
                {
                    ProcessStartInfo startInfo = new ProcessStartInfo();
                    InvokeRouting(startInfo, CaptainWithCredential("example-provider-base", "example-provider/claude-fable-5", "captain-key-not-a-real-credential", "https://proxy.example.test"), RegistryWithExampleProvider());

                    AssertEqual("https://proxy.example.test", startInfo.Environment["ANTHROPIC_BASE_URL"],
                        "A captain's base URL must override the default provider endpoint");
                    AssertEqual("captain-key-not-a-real-credential", startInfo.Environment["ANTHROPIC_API_KEY"],
                        "The per-captain key must still be supplied alongside the base URL");
                    return Task.CompletedTask;
                });

                await RunTest("CaptainApiKey_PresentWithoutEnvironmentKey_StillRoutes", () =>
                {
                    // The per-captain key must work even when the host carries no provider key at all.
                    Environment.SetEnvironmentVariable("EXAMPLE_PROVIDER_KEY", null);
                    ProcessStartInfo startInfo = new ProcessStartInfo();
                    InvokeRouting(startInfo, CaptainWithCredential("example-provider-standalone", "example-provider/claude-fable-5", "captain-key-not-a-real-credential"), RegistryWithExampleProvider());

                    AssertEqual("captain-key-not-a-real-credential", startInfo.Environment["ANTHROPIC_API_KEY"],
                        "A per-captain key must route without any host-level key present");
                    AssertTrue(startInfo.Environment.ContainsKey("ANTHROPIC_BASE_URL"),
                        "The endpoint must be set when the per-captain key routes the captain");
                    Environment.SetEnvironmentVariable("EXAMPLE_PROVIDER_KEY", "test-key-not-a-real-credential");
                    return Task.CompletedTask;
                });

                await RunTest("MissingKey_LeavesTheCaptainOnTheNativeEndpoint", () =>
                {
                    // Half-configuring the captain would fail every step and read as a provider outage.
                    Environment.SetEnvironmentVariable("EXAMPLE_PROVIDER_KEY", null);
                    ProcessStartInfo startInfo = new ProcessStartInfo();
                    InvokeRouting(startInfo, CaptainWithModel("example-provider-4", "example-provider/claude-fable-5"), RegistryWithExampleProvider());

                    AssertFalse(startInfo.Environment.ContainsKey("ANTHROPIC_BASE_URL"),
                        "With no key the captain must not be redirected to a half-configured endpoint");
                    Environment.SetEnvironmentVariable("EXAMPLE_PROVIDER_KEY", "test-key-not-a-real-credential");
                    return Task.CompletedTask;
                });

                await RunTest("NullModelCaptain_IsIgnored", () =>
                {
                    ProcessStartInfo startInfo = new ProcessStartInfo();
                    InvokeRouting(startInfo, CaptainWithModel("no-model", null));
                    AssertFalse(startInfo.Environment.ContainsKey("ANTHROPIC_BASE_URL"),
                        "A captain with no model must not be routed anywhere");

                    ProcessStartInfo nullCaptain = new ProcessStartInfo();
                    InvokeRouting(nullCaptain, null);
                    AssertFalse(nullCaptain.Environment.ContainsKey("ANTHROPIC_BASE_URL"),
                        "A null captain must not throw or route");
                    return Task.CompletedTask;
                });

                await RunTest("CustomEndpointCaptain_IsRoutedWhenItCarriesUrlAndKey", () =>
                {
                    // The custom-endpoint path: a native model name with no provider prefix routes
                    // to the captain's own endpoint when both the base URL and the key are set.
                    // This is how a fable judge is served by an Anthropic-compatible provider
                    // such as example-provider without renaming the model.
                    ProcessStartInfo startInfo = new ProcessStartInfo();
                    InvokeRouting(startInfo, CaptainWithCredential(
                        "example-provider-judge",
                        "claude-fable-5",
                        "captain-key-not-a-real-credential",
                        "https://example-provider"));

                    AssertEqual("https://example-provider", startInfo.Environment["ANTHROPIC_BASE_URL"],
                        "A custom-endpoint captain must be pointed at its own base URL");
                    AssertEqual("captain-key-not-a-real-credential", startInfo.Environment["ANTHROPIC_API_KEY"],
                        "The custom-endpoint captain's own key must be supplied");
                    return Task.CompletedTask;
                });

                await RunTest("CustomEndpointCaptain_WithoutBaseUrl_IsLeftAlone", () =>
                {
                    // A key alone cannot name an endpoint, so the captain stays native rather
                    // than being launched half-configured.
                    ProcessStartInfo startInfo = new ProcessStartInfo();
                    InvokeRouting(startInfo, CaptainWithCredential("key-only", "claude-fable-5", "captain-key-not-a-real-credential"));

                    AssertFalse(startInfo.Environment.ContainsKey("ANTHROPIC_BASE_URL"),
                        "A key without a base URL must not redirect the captain");
                    AssertFalse(startInfo.Environment.ContainsKey("ANTHROPIC_API_KEY"),
                        "A key without a base URL must not be injected");
                    return Task.CompletedTask;
                });

                await RunTest("CustomEndpointCaptain_WithoutKey_IsLeftAlone", () =>
                {
                    ProcessStartInfo startInfo = new ProcessStartInfo();
                    InvokeRouting(startInfo, CaptainWithCredential("url-only", "claude-fable-5", null, "https://example-provider"));

                    AssertFalse(startInfo.Environment.ContainsKey("ANTHROPIC_BASE_URL"),
                        "A base URL without a key must not redirect the captain");
                    return Task.CompletedTask;
                });

                await RunTest("UnregisteredProviderPrefix_IsLeftAlone", () =>
                {
                    // Only registered provider prefixes route; an unregistered namespace is
                    // presumed to belong to the runtime or the operator's own config.
                    ProcessStartInfo startInfo = new ProcessStartInfo();
                    InvokeRouting(startInfo, CaptainWithCredential(
                        "unregistered",
                        "example-provider/claude-fable-5",
                        "captain-key-not-a-real-credential",
                        "https://example-provider"),
                        null);

                    AssertFalse(startInfo.Environment.ContainsKey("ANTHROPIC_BASE_URL"),
                        "An unregistered provider prefix must not route even with captain credentials");
                    return Task.CompletedTask;
                });

                await RunTest("DefaultRegistry_IsEmpty_NoProviderRoutesWithoutRegistration", () =>
                {
                    // The built-in default registry is empty: no provider routes until the
                    // operator registers one in settings.json.
                    ProcessStartInfo startInfo = new ProcessStartInfo();
                    InvokeRouting(startInfo, CaptainWithModel("no-registry", "example-provider/claude-fable-5"));

                    AssertFalse(startInfo.Environment.ContainsKey("ANTHROPIC_BASE_URL"),
                        "With an empty default registry, no prefixed model routes");
                    return Task.CompletedTask;
                });

                await RunTest("ResolvedModel_ApiModelId_StripsTheArmadaNamespacePrefix", () =>
                {
                    // The prefix is Armada's selection namespace; the provider API serves the id
                    // after it. example-provider serves "claude-fable-5", not "example-provider/claude-fable-5" --
                    // passing the prefixed form yields "No available channel" from the provider.
                    ResolvedModelProvider? resolved = ModelProviderResolver.Resolve(
                        CaptainWithModel("example-provider-5", "example-provider/claude-fable-5"),
                        null,
                        RegistryWithExampleProvider());

                    AssertNotNull(resolved, "A registered provider must resolve");
                    AssertEqual("claude-fable-5", resolved!.ApiModelId,
                        "The provider-facing model id must omit the namespace prefix");
                    return Task.CompletedTask;
                });

                await RunTest("ResolvedModel_CustomEndpoint_ApiModelIdKeepsThePlainId", () =>
                {
                    ResolvedModelProvider? resolved = ModelProviderResolver.Resolve(
                        CaptainWithCredential("custom-1", "claude-fable-5", "captain-key-not-a-real-credential", "https://example-provider"),
                        null,
                        null);

                    AssertNotNull(resolved, "A custom-endpoint captain must resolve");
                    AssertEqual("claude-fable-5", resolved!.ApiModelId,
                        "A plain model id must pass through verbatim");
                    return Task.CompletedTask;
                });
            }
            finally
            {
                Environment.SetEnvironmentVariable("EXAMPLE_PROVIDER_KEY", originalCun);
            }
        }
    }
}
