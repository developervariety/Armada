namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Diagnostics;
    using System.Reflection;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Runtimes;
    using Armada.Test.Common;

    /// <summary>
    /// Tests that a Zyloo-served Claude captain is pointed at Zyloo's Anthropic-native endpoint, and
    /// that doing so cannot disturb a captain on the native Anthropic account.
    /// </summary>
    public class ClaudeCodeZylooRoutingTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Claude Code Zyloo Routing";

        private static void InvokeRouting(ProcessStartInfo startInfo, Captain? captain)
        {
            MethodInfo? method = typeof(ClaudeCodeRuntime).GetMethod(
                "ApplyZylooRouting",
                BindingFlags.Static | BindingFlags.NonPublic);
            if (method == null) throw new InvalidOperationException("Could not find ApplyZylooRouting.");
            method.Invoke(null, new object?[] { startInfo, captain });
        }

        private static Captain CaptainWithModel(string name, string? model)
        {
            Captain captain = new Captain(name);
            captain.Model = model;
            return captain;
        }

        /// <summary>Runs the suite.</summary>
        protected override async Task RunTestsAsync()
        {
            string? original = Environment.GetEnvironmentVariable("ZYLOO_KEY");
            Environment.SetEnvironmentVariable("ZYLOO_KEY", "test-key-not-a-real-credential");

            try
            {
                await RunTest("ZylooModel_IsRoutedToTheAnthropicNativeEndpoint", () =>
                {
                    ProcessStartInfo startInfo = new ProcessStartInfo();
                    InvokeRouting(startInfo, CaptainWithModel("zyloo-1", "zyloo/claude-opus-4-8"));

                    AssertEqual("https://api.zyloo.io", startInfo.Environment["ANTHROPIC_BASE_URL"],
                        "A Zyloo captain must be pointed at Zyloo's Anthropic-native endpoint");
                    AssertEqual("test-key-not-a-real-credential", startInfo.Environment["ANTHROPIC_AUTH_TOKEN"],
                        "The Zyloo key must be supplied as the Anthropic auth token");
                    return Task.CompletedTask;
                });

                await RunTest("NativeCaptain_IsLeftEntirelyAlone", () =>
                {
                    // The whole design rests on this: a native Claude captain launched beside a Zyloo
                    // one must keep its own account and endpoint.
                    ProcessStartInfo startInfo = new ProcessStartInfo();
                    InvokeRouting(startInfo, CaptainWithModel("native-1", "claude-opus-4-8"));

                    AssertFalse(startInfo.Environment.ContainsKey("ANTHROPIC_BASE_URL"),
                        "A native Claude captain must never be redirected to another endpoint");
                    AssertFalse(startInfo.Environment.ContainsKey("ANTHROPIC_AUTH_TOKEN"),
                        "A native Claude captain must never receive the Zyloo credential");
                    return Task.CompletedTask;
                });

                await RunTest("ConcurrentLaunches_DoNotLeakBetweenCaptains", () =>
                {
                    // Two captains launched from the same admiral get independent environments.
                    ProcessStartInfo zyloo = new ProcessStartInfo();
                    ProcessStartInfo native = new ProcessStartInfo();

                    InvokeRouting(zyloo, CaptainWithModel("zyloo-2", "zyloo/claude-fable-5"));
                    InvokeRouting(native, CaptainWithModel("native-2", "claude-fable-5"));

                    AssertTrue(zyloo.Environment.ContainsKey("ANTHROPIC_BASE_URL"),
                        "The Zyloo captain keeps its redirect");
                    AssertFalse(native.Environment.ContainsKey("ANTHROPIC_BASE_URL"),
                        "The native captain launched alongside it must be unaffected");
                    return Task.CompletedTask;
                });

                await RunTest("InheritedApiKey_IsClearedForZylooCaptainsOnly", () =>
                {
                    // ANTHROPIC_API_KEY would win over the auth token inside the CLI, so it must be
                    // cleared for the Zyloo child -- and left intact for everyone else.
                    ProcessStartInfo zyloo = new ProcessStartInfo();
                    zyloo.Environment["ANTHROPIC_API_KEY"] = "inherited-native-key";
                    InvokeRouting(zyloo, CaptainWithModel("zyloo-3", "zyloo/claude-opus-4-7"));
                    AssertFalse(zyloo.Environment.ContainsKey("ANTHROPIC_API_KEY"),
                        "A stale inherited API key must not override the Zyloo token");

                    ProcessStartInfo native = new ProcessStartInfo();
                    native.Environment["ANTHROPIC_API_KEY"] = "inherited-native-key";
                    InvokeRouting(native, CaptainWithModel("native-3", "claude-opus-4-7"));
                    AssertEqual("inherited-native-key", native.Environment["ANTHROPIC_API_KEY"],
                        "A native captain's own credential must be left untouched");
                    return Task.CompletedTask;
                });

                await RunTest("MissingKey_LeavesTheCaptainOnTheNativeEndpoint", () =>
                {
                    // Half-configuring the captain would fail every step and read as a provider outage.
                    Environment.SetEnvironmentVariable("ZYLOO_KEY", null);
                    ProcessStartInfo startInfo = new ProcessStartInfo();
                    InvokeRouting(startInfo, CaptainWithModel("zyloo-4", "zyloo/claude-opus-4-8"));

                    AssertFalse(startInfo.Environment.ContainsKey("ANTHROPIC_BASE_URL"),
                        "With no key the captain must not be redirected to a half-configured endpoint");
                    Environment.SetEnvironmentVariable("ZYLOO_KEY", "test-key-not-a-real-credential");
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
            }
            finally
            {
                Environment.SetEnvironmentVariable("ZYLOO_KEY", original);
            }
        }
    }
}
