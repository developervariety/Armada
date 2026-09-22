namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Diagnostics;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using Armada.Runtimes;
    using Armada.Test.Common;
    using SyslogLogging;

    /// <summary>
    /// Tests that a Claude captain served by an external provider is pointed at that
    /// provider's Anthropic-native endpoint, and that doing so cannot disturb a captain
    /// on the native Anthropic account.
    /// </summary>
    public class ClaudeCodeProviderRoutingTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Claude Code Provider Routing";

        private const string _ProviderKeyVariable = "EXAMPLE_PROVIDER_KEY";
        private const string _ProviderKeyValue = "test-key-not-a-real-credential";

        /// <summary>
        /// Apply the environment a Claude Code captain launch applies, through the runtime's own
        /// launch environment hook, with the given provider registry.
        /// </summary>
        private static void InvokeRouting(ProcessStartInfo startInfo, Captain? captain, ModelProvidersSettings? providers = null)
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            LaunchEnvironmentClaudeCodeRuntime runtime = new LaunchEnvironmentClaudeCodeRuntime(logging, providers);
            runtime.ApplyLaunchEnvironment(startInfo, captain);
        }

        private sealed class LaunchEnvironmentClaudeCodeRuntime : ClaudeCodeRuntime
        {
            public LaunchEnvironmentClaudeCodeRuntime(LoggingModule logging, ModelProvidersSettings? providers)
                : base(logging, providers)
            {
            }

            public void ApplyLaunchEnvironment(ProcessStartInfo startInfo, Captain? captain)
            {
                ApplyEnvironment(startInfo, captain);
            }
        }

        /// <summary>
        /// Start info whose environment holds only what routing writes. A new ProcessStartInfo
        /// copies the environment of the process running the suite, so an ANTHROPIC_* variable the
        /// operator exported would read as a routing write and every "left alone" assertion would
        /// fail for a reason the routing code does not control.
        /// </summary>
        private static ProcessStartInfo RoutingOnlyStartInfo()
        {
            ProcessStartInfo startInfo = new ProcessStartInfo();
            startInfo.Environment.Clear();
            return startInfo;
        }

        /// <summary>
        /// Run a body with a process environment variable set, restoring the previous value even
        /// when an assertion throws, so one failure cannot change the inputs of later tests.
        /// </summary>
        private static void WithEnvironmentVariable(string name, string? value, Action body)
        {
            string? previous = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
            try
            {
                body();
            }
            finally
            {
                Environment.SetEnvironmentVariable(name, previous);
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
                ApiKeyEnv = _ProviderKeyVariable
            };
            return registry;
        }

        /// <summary>Runs the suite.</summary>
        protected override async Task RunTestsAsync()
        {
            string? originalProviderKey = Environment.GetEnvironmentVariable(_ProviderKeyVariable);
            Environment.SetEnvironmentVariable(_ProviderKeyVariable, _ProviderKeyValue);

            try
            {
                await RunTest("ProviderModel_IsRoutedToTheProviderEndpoint", () =>
                {
                    ProcessStartInfo startInfo = RoutingOnlyStartInfo();
                    InvokeRouting(startInfo, CaptainWithModel("example-provider-1", "example-provider/claude-fable-5"), RegistryWithExampleProvider());

                    AssertEqual("https://example-provider", startInfo.Environment["ANTHROPIC_BASE_URL"],
                        "A provider-prefixed captain must be pointed at the provider's Anthropic-native endpoint");
                    AssertEqual(_ProviderKeyValue, startInfo.Environment["ANTHROPIC_API_KEY"],
                        "The provider key must be supplied as ANTHROPIC_API_KEY, the form providers document");
                    return Task.CompletedTask;
                });

                await RunTest("NativeCaptain_IsLeftEntirelyAlone", () =>
                {
                    // The whole design rests on this: a native Claude captain launched beside an
                    // external-provider one must keep its own account and endpoint.
                    ProcessStartInfo startInfo = RoutingOnlyStartInfo();
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
                    ProcessStartInfo provider = RoutingOnlyStartInfo();
                    ProcessStartInfo native = RoutingOnlyStartInfo();

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
                    ProcessStartInfo provider = RoutingOnlyStartInfo();
                    provider.Environment["ANTHROPIC_AUTH_TOKEN"] = "inherited-native-token";
                    InvokeRouting(provider, CaptainWithModel("example-provider-3", "example-provider/claude-fable-5"), RegistryWithExampleProvider());
                    AssertFalse(provider.Environment.ContainsKey("ANTHROPIC_AUTH_TOKEN"),
                        "A stale inherited auth token must not override the provider API key");

                    ProcessStartInfo native = RoutingOnlyStartInfo();
                    native.Environment["ANTHROPIC_AUTH_TOKEN"] = "inherited-native-token";
                    InvokeRouting(native, CaptainWithModel("native-3", "claude-opus-4-7"));
                    AssertEqual("inherited-native-token", native.Environment["ANTHROPIC_AUTH_TOKEN"],
                        "A native captain's own credential must be left untouched");
                    return Task.CompletedTask;
                });

                await RunTest("AmbientAnthropicEnvironment_NativeCaptainInheritsIt_RoutedCaptainReplacesIt", () =>
                {
                    // Launch start info inherits the admiral's environment. A native captain keeps the
                    // operator's own endpoint and key exactly as inherited; a routed captain must
                    // replace both, so the operator's values can never reach the provider.
                    WithEnvironmentVariable("ANTHROPIC_BASE_URL", "https://operator-endpoint.example.test", () =>
                    {
                        WithEnvironmentVariable("ANTHROPIC_API_KEY", "operator-key-not-a-real-credential", () =>
                        {
                            ProcessStartInfo native = new ProcessStartInfo();
                            InvokeRouting(native, CaptainWithModel("native-ambient", "claude-fable-5"));
                            AssertEqual("https://operator-endpoint.example.test", native.Environment["ANTHROPIC_BASE_URL"],
                                "A native captain inherits the operator endpoint unchanged");
                            AssertEqual("operator-key-not-a-real-credential", native.Environment["ANTHROPIC_API_KEY"],
                                "A native captain inherits the operator key unchanged");

                            ProcessStartInfo routed = new ProcessStartInfo();
                            InvokeRouting(routed, CaptainWithModel("example-provider-ambient", "example-provider/claude-fable-5"), RegistryWithExampleProvider());
                            AssertEqual("https://example-provider", routed.Environment["ANTHROPIC_BASE_URL"],
                                "A routed captain must not keep the operator endpoint");
                            AssertEqual(_ProviderKeyValue, routed.Environment["ANTHROPIC_API_KEY"],
                                "A routed captain must not send the operator key to the provider");
                        });
                    });
                    return Task.CompletedTask;
                });

                await RunTest("CaptainApiKey_WinsOverTheHostEnvironmentKey", () =>
                {
                    // Two subscriptions run side by side: a captain's own key must beat the
                    // host-level key that serves the other subscription.
                    ProcessStartInfo startInfo = RoutingOnlyStartInfo();
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
                    ProcessStartInfo startInfo = RoutingOnlyStartInfo();
                    InvokeRouting(startInfo, CaptainWithCredential("example-provider-env", "example-provider/claude-fable-5", null), RegistryWithExampleProvider());

                    AssertEqual(_ProviderKeyValue, startInfo.Environment["ANTHROPIC_API_KEY"],
                        "The host-level key must remain the fallback for captains without a key");
                    return Task.CompletedTask;
                });

                await RunTest("CaptainApiBaseUrl_OverridesTheDefaultEndpoint", () =>
                {
                    ProcessStartInfo startInfo = RoutingOnlyStartInfo();
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
                    WithEnvironmentVariable(_ProviderKeyVariable, null, () =>
                    {
                        ProcessStartInfo startInfo = RoutingOnlyStartInfo();
                        InvokeRouting(startInfo, CaptainWithCredential("example-provider-standalone", "example-provider/claude-fable-5", "captain-key-not-a-real-credential"), RegistryWithExampleProvider());

                        AssertEqual("captain-key-not-a-real-credential", startInfo.Environment["ANTHROPIC_API_KEY"],
                            "A per-captain key must route without any host-level key present");
                        AssertTrue(startInfo.Environment.ContainsKey("ANTHROPIC_BASE_URL"),
                            "The endpoint must be set when the per-captain key routes the captain");
                    });
                    return Task.CompletedTask;
                });

                await RunTest("MissingKey_LeavesTheCaptainOnTheNativeEndpoint", () =>
                {
                    // Half-configuring the captain would fail every step and read as a provider outage.
                    WithEnvironmentVariable(_ProviderKeyVariable, null, () =>
                    {
                        ProcessStartInfo startInfo = RoutingOnlyStartInfo();
                        InvokeRouting(startInfo, CaptainWithModel("example-provider-4", "example-provider/claude-fable-5"), RegistryWithExampleProvider());

                        AssertFalse(startInfo.Environment.ContainsKey("ANTHROPIC_BASE_URL"),
                            "With no key the captain must not be redirected to a half-configured endpoint");
                    });
                    return Task.CompletedTask;
                });

                await RunTest("NullModelCaptain_IsIgnored", () =>
                {
                    ProcessStartInfo startInfo = RoutingOnlyStartInfo();
                    InvokeRouting(startInfo, CaptainWithModel("no-model", null));
                    AssertFalse(startInfo.Environment.ContainsKey("ANTHROPIC_BASE_URL"),
                        "A captain with no model must not be routed anywhere");

                    ProcessStartInfo nullCaptain = RoutingOnlyStartInfo();
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
                    ProcessStartInfo startInfo = RoutingOnlyStartInfo();
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
                    ProcessStartInfo startInfo = RoutingOnlyStartInfo();
                    InvokeRouting(startInfo, CaptainWithCredential("key-only", "claude-fable-5", "captain-key-not-a-real-credential"));

                    AssertFalse(startInfo.Environment.ContainsKey("ANTHROPIC_BASE_URL"),
                        "A key without a base URL must not redirect the captain");
                    AssertFalse(startInfo.Environment.ContainsKey("ANTHROPIC_API_KEY"),
                        "A key without a base URL must not be injected");
                    return Task.CompletedTask;
                });

                await RunTest("CustomEndpointCaptain_WithoutKey_IsLeftAlone", () =>
                {
                    ProcessStartInfo startInfo = RoutingOnlyStartInfo();
                    InvokeRouting(startInfo, CaptainWithCredential("url-only", "claude-fable-5", null, "https://example-provider"));

                    AssertFalse(startInfo.Environment.ContainsKey("ANTHROPIC_BASE_URL"),
                        "A base URL without a key must not redirect the captain");
                    return Task.CompletedTask;
                });

                await RunTest("UnregisteredProviderPrefix_IsLeftAlone", () =>
                {
                    // Only registered provider prefixes route; an unregistered namespace is
                    // presumed to belong to the runtime or the operator's own config.
                    ProcessStartInfo startInfo = RoutingOnlyStartInfo();
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
                    ProcessStartInfo startInfo = RoutingOnlyStartInfo();
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
                Environment.SetEnvironmentVariable(_ProviderKeyVariable, originalProviderKey);
            }
        }
    }
}
