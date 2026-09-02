namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Reflection;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using Armada.Runtimes;
    using Armada.Test.Common;
    using SyslogLogging;

    /// <summary>
    /// Tests that a Codex captain served by an external provider is routed through a
    /// <c>--profile</c> config layer (codex ignores base-URL environment variables) and
    /// that native Codex captains are untouched.
    /// </summary>
    public class CodexProviderRoutingTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Codex Provider Routing";

        private static CodexRuntime CreateRuntime()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return new CodexRuntime(logging);
        }

        private static Captain CaptainWithCredential(string name, string? model, string? apiKey, string? apiBaseUrl)
        {
            Captain captain = new Captain(name);
            captain.Model = model;
            captain.ApiKey = apiKey;
            captain.ApiBaseUrl = apiBaseUrl;
            return captain;
        }

        private static void InvokeApplyEnvironment(CodexRuntime runtime, ProcessStartInfo startInfo, Captain? captain)
        {
            MethodInfo? method = typeof(CodexRuntime).GetMethod(
                "ApplyEnvironment",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (method == null) throw new InvalidOperationException("Could not find ApplyEnvironment.");
            method.Invoke(runtime, new object?[] { startInfo, captain, captain?.Model });
        }

        private static List<string> InvokeBuildArguments(CodexRuntime runtime, string? model, Captain? captain)
        {
            MethodInfo? method = typeof(CodexRuntime).GetMethod(
                "BuildArguments",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (method == null) throw new InvalidOperationException("Could not find BuildArguments.");
            List<string>? args = method.Invoke(runtime, new object?[] { "/tmp", "prompt", model, null, captain }) as List<string>;
            return args ?? new List<string>();
        }

        /// <summary>Runs the suite.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("CustomEndpointCaptain_IsRoutedViaProviderKeyAndProfile", () =>
            {
                // The luna captains carry a plain model id plus their own key and base URL.
                // Codex must receive the credential through ARMADA_PROVIDER_KEY and the
                // provider layer through --profile custom.
                CodexRuntime runtime = CreateRuntime();
                Captain captain = CaptainWithCredential(
                    "luna-cun-ai",
                    "gpt-5.6-luna",
                    "captain-key-not-a-real-credential",
                    "https://cun.ai/v1");

                ProcessStartInfo startInfo = new ProcessStartInfo();
                InvokeApplyEnvironment(runtime, startInfo, captain);

                AssertEqual("captain-key-not-a-real-credential", startInfo.Environment["ARMADA_PROVIDER_KEY"],
                    "The routed credential must be exposed as ARMADA_PROVIDER_KEY");
                AssertFalse(startInfo.Environment.ContainsKey("CODEX_API_KEY"),
                    "An inherited CODEX_API_KEY must be cleared for the routed child");

                List<string> args = InvokeBuildArguments(runtime, captain.Model, captain);
                int modelIndex = args.IndexOf("--model");
                AssertTrue(modelIndex >= 0, "--model must be present");
                AssertEqual("gpt-5.6-luna", args[modelIndex + 1], "the plain model id must pass through verbatim");
                int profileIndex = args.IndexOf("--profile");
                AssertTrue(profileIndex >= 0, "--profile must be present for a routed captain");
                AssertEqual("custom-cun-ai", args[profileIndex + 1], "a custom-endpoint captain uses a host-derived profile layer");
                return Task.CompletedTask;
            });

            await RunTest("PrefixedProviderModel_ProfileUsesProviderId", () =>
            {
                // A registered-provider model id routes through the provider's own profile name.
                ModelProvidersSettings registry = new ModelProvidersSettings();
                registry.Providers["cun-ai"] = new ModelProviderSettings
                {
                    Name = "cun-ai",
                    BaseUrl = "https://cun.ai",
                    ApiKeyEnv = "CUN_AI_KEY"
                };
                LoggingModule logging = new LoggingModule();
                logging.Settings.EnableConsole = false;
                CodexRuntime runtime = new CodexRuntime(logging, registry);
                Captain captain = CaptainWithCredential(
                    "prefixed",
                    "cun-ai/gpt-5.6-luna",
                    "captain-key-not-a-real-credential",
                    "https://cun.ai/v1");

                List<string> args = InvokeBuildArguments(runtime, captain.Model, captain);
                int profileIndex = args.IndexOf("--profile");
                AssertTrue(profileIndex >= 0, "--profile must be present");
                AssertEqual("cun-ai", args[profileIndex + 1], "the profile name must be the provider id");
                return Task.CompletedTask;
            });

            await RunTest("CustomEndpointCaptains_OnDifferentProviders_UseDistinctProfiles", () =>
            {
                // Two custom-endpoint captains on different providers must never share one
                // profile file: the last launcher would overwrite the other's base URL.
                CodexRuntime runtime = CreateRuntime();
                Captain luna = CaptainWithCredential("luna", "gpt-5.6-luna", "k", "https://cun.ai/v1");
                Captain external = CaptainWithCredential("external-sol", "gpt-5.6-sol", "k", "https://api.example.com/v1");

                List<string> lunaArgs = InvokeBuildArguments(runtime, luna.Model, luna);
                List<string> externalArgs = InvokeBuildArguments(runtime, external.Model, external);

                int lunaProfile = lunaArgs.IndexOf("--profile");
                int externalProfile = externalArgs.IndexOf("--profile");
                AssertTrue(lunaProfile >= 0 && externalProfile >= 0, "both routed captains must carry a profile");
                AssertEqual("custom-cun-ai", lunaArgs[lunaProfile + 1], "the cun-ai captain uses its own profile");
                AssertEqual("custom-api-example-com", externalArgs[externalProfile + 1], "the external captain uses its own profile");
                AssertNotEqual(lunaArgs[lunaProfile + 1], externalArgs[externalProfile + 1],
                    "different providers must not share a profile file");
                return Task.CompletedTask;
            });

            await RunTest("NativeCaptain_IsLeftEntirelyAlone", () =>
            {
                CodexRuntime runtime = CreateRuntime();
                Captain captain = new Captain("native-sol");
                captain.Model = "gpt-5.6-sol";

                ProcessStartInfo startInfo = new ProcessStartInfo();
                InvokeApplyEnvironment(runtime, startInfo, captain);
                AssertFalse(startInfo.Environment.ContainsKey("ARMADA_PROVIDER_KEY"),
                    "A native captain must not receive the provider credential");

                List<string> args = InvokeBuildArguments(runtime, captain.Model, captain);
                AssertFalse(args.Contains("--profile"), "A native captain must not get a profile layer");
                return Task.CompletedTask;
            });
        }
    }
}
