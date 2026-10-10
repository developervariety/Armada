namespace Armada.Test.Runtimes.Suites
{
    using System.Diagnostics;
    using Armada.Core.Enums;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using Armada.Runtimes;
    using Armada.Test.Common;
    using SyslogLogging;

    /// <summary>
    /// A captain process never inherits an admiral secret, whatever its runtime, while the variables a launch
    /// sets on purpose and the admiral's non-secret variables still reach it.
    /// </summary>
    public class CaptainEnvironmentScrubTests : TestSuite
    {
        public override string Name => "Captain Environment Scrub Tests";

        private const string NamedKey = "ARMADA_SCRUBTEST_KEY";
        private const string NamedTokenVariable = "ARMADA_SCRUBTEST_MCP_TOKEN";
        private const string ConfiguredName = "EXAMPLE_PROVIDER_SCRUBTEST";
        private const string PlainName = "ARMADA_SCRUBTEST_ROOT";
        private const string FixtureName = "ARMADA_TEST_SCRUBTEST_KEY";
        private const string LaunchName = "ARMADA_MCP_TOKEN";

        private static readonly AgentRuntimeEnum[] _CliRuntimes = new AgentRuntimeEnum[]
        {
            AgentRuntimeEnum.ClaudeCode,
            AgentRuntimeEnum.Codex,
            AgentRuntimeEnum.Cursor,
            AgentRuntimeEnum.OpenCode,
            AgentRuntimeEnum.Mux,
            AgentRuntimeEnum.Gemini
        };

        protected override async Task RunTestsAsync()
        {
            await RunTest("Every CLI Runtime Removes Admiral Secrets From The Captain Environment", () =>
            {
                WithParentEnvironment(() =>
                {
                    AgentRuntimeFactory factory = new AgentRuntimeFactory(CreateLogging());
                    factory.AdmiralSecretNames = () => new List<string> { ConfiguredName };
                    foreach (AgentRuntimeEnum runtimeType in _CliRuntimes)
                    {
                        BaseAgentRuntime runtime = (BaseAgentRuntime)factory.Create(runtimeType);
                        ProcessStartInfo startInfo = new ProcessStartInfo();
                        AssertTrue(startInfo.Environment.ContainsKey(NamedKey), runtimeType + ": the parent secret is inherited before the scrub");

                        runtime.PrepareCaptainEnvironment(startInfo, new Dictionary<string, string> { { LaunchName, "mission-scoped" } }, null, null, null);

                        AssertFalse(startInfo.Environment.ContainsKey(NamedKey), runtimeType + ": a credential-named admiral variable is removed");
                        AssertFalse(startInfo.Environment.ContainsKey(NamedTokenVariable), runtimeType + ": a token-named admiral variable is removed");
                        AssertFalse(startInfo.Environment.ContainsKey(ConfiguredName), runtimeType + ": a configured secret name is removed");
                        AssertEqual("plain", startInfo.Environment[PlainName], runtimeType + ": a non-secret admiral variable is still inherited");
                        AssertEqual("fixture", startInfo.Environment[FixtureName], runtimeType + ": a test fixture variable is still inherited");
                        AssertEqual("mission-scoped", startInfo.Environment[LaunchName], runtimeType + ": a variable the launch sets survives the scrub");
                    }
                });
            });

            await RunTest("A Runtime Without Configured Names Still Removes Credential-Named Admiral Variables", () =>
            {
                WithParentEnvironment(() =>
                {
                    BaseAgentRuntime runtime = (BaseAgentRuntime)new AgentRuntimeFactory(CreateLogging()).Create(AgentRuntimeEnum.Codex);
                    ProcessStartInfo startInfo = new ProcessStartInfo();
                    runtime.PrepareCaptainEnvironment(startInfo, null, null, null, null);
                    AssertFalse(startInfo.Environment.ContainsKey(NamedKey), "the name rule needs no configuration");
                    AssertEqual("secret-3", startInfo.Environment[ConfiguredName], "an unconfigured, non-admiral name is not guessed at");
                });
            });

            await RunTest("A Failing Name Source Falls Back To The Credential-Name Rule", () =>
            {
                WithParentEnvironment(() =>
                {
                    BaseAgentRuntime runtime = (BaseAgentRuntime)new AgentRuntimeFactory(CreateLogging()).Create(AgentRuntimeEnum.ClaudeCode);
                    runtime.AdmiralSecretNames = () => throw new InvalidOperationException("settings unavailable");
                    ProcessStartInfo startInfo = new ProcessStartInfo();
                    runtime.PrepareCaptainEnvironment(startInfo, null, null, null, null);
                    AssertFalse(startInfo.Environment.ContainsKey(NamedKey), "the launch still scrubs when the configured names cannot be read");
                });
            });

            await RunTest("Credential Name Rule Matches Whole Name Parts In The Admiral Namespace", () =>
            {
                AssertTrue(CaptainEnvironmentScrub.IsAdmiralSecret("ARMADA_EXAMPLE_KEY", null), "_KEY");
                AssertTrue(CaptainEnvironmentScrub.IsAdmiralSecret("ARMADA_EXAMPLE_MCP_TOKEN", null), "_TOKEN");
                AssertTrue(CaptainEnvironmentScrub.IsAdmiralSecret("armada_example_password", null), "case-insensitive");
                AssertTrue(CaptainEnvironmentScrub.IsAdmiralSecret("ARMADA_DB_PASS", null), "_PASS");
                AssertFalse(CaptainEnvironmentScrub.IsAdmiralSecret("ARMADA_GIT_TIMEOUT_MS", null), "a setting is not a secret");
                AssertFalse(CaptainEnvironmentScrub.IsAdmiralSecret("ARMADA_PASSPORT_ROOT", null), "a part must match whole, not as a prefix");
                AssertFalse(CaptainEnvironmentScrub.IsAdmiralSecret("ARMADA_TEST_CURSOR_ACCOUNT_KEY", null), "the test namespace carries fixtures to fake agents");
                AssertFalse(CaptainEnvironmentScrub.IsAdmiralSecret("OPENAI_API_KEY", null), "outside the admiral namespace only configured names match");
                AssertTrue(CaptainEnvironmentScrub.IsAdmiralSecret("OPENAI_API_KEY", new List<string> { "openai_api_key" }), "a configured name matches case-insensitively");
                AssertFalse(CaptainEnvironmentScrub.IsAdmiralSecret("", null), "empty name");
            });

            await RunTest("Configured Names Come From Provider Keys The Typed Decision Key And Extra Names", () =>
            {
                ArmadaSettings settings = new ArmadaSettings();
                settings.ModelProviders.Providers["example-provider"] = new ModelProviderSettings { ApiKeyEnv = "EXAMPLE_PROVIDER_KEY" };
                settings.TypedDecisions.ApiKeyEnv = "EXAMPLE_DECISION_KEY";
                settings.CaptainEnvironmentScrubNames = new List<string> { "EXAMPLE_EXTRA_SECRET", " " };

                IReadOnlyCollection<string> names = CaptainEnvironmentScrub.ConfiguredNames(settings);
                AssertTrue(names.Contains("EXAMPLE_PROVIDER_KEY"), "provider key variable");
                AssertTrue(names.Contains("EXAMPLE_DECISION_KEY"), "typed-decision key variable");
                AssertTrue(names.Contains("EXAMPLE_EXTRA_SECRET"), "extra configured name");
                AssertEqual(3, names.Count, "blank names are ignored");
                AssertEqual(0, CaptainEnvironmentScrub.ConfiguredNames(null).Count, "no settings, no names");
            });

            await RunTest("Claude Code Captains Are Refused The Environment Listing Commands", () =>
            {
                List<string> args = new InspectableArgs().Build();
                int index = args.IndexOf("--disallowedTools");
                AssertTrue(index >= 0, "--disallowedTools flag missing");
                List<string> rules = args.Skip(index + 1).TakeWhile(arg => !arg.StartsWith("--", StringComparison.Ordinal)).ToList();
                AssertTrue(rules.Contains("Bash(env)"), "bare env is refused");
                AssertTrue(rules.Contains("Bash(printenv)"), "bare printenv is refused");
                AssertTrue(rules.Contains("Bash(printenv:*)"), "printenv of one variable is refused");
                AssertFalse(rules.Any(rule => rule.StartsWith("Bash(env:", StringComparison.Ordinal)), "env NAME=value cmd stays allowed");
            });

            await Task.CompletedTask;
        }

        private static void WithParentEnvironment(Action body)
        {
            Dictionary<string, string> values = new Dictionary<string, string>
            {
                { NamedKey, "secret-1" },
                { NamedTokenVariable, "secret-2" },
                { ConfiguredName, "secret-3" },
                { PlainName, "plain" },
                { FixtureName, "fixture" }
            };
            Dictionary<string, string?> prior = new Dictionary<string, string?>();
            foreach (KeyValuePair<string, string> entry in values)
            {
                prior[entry.Key] = Environment.GetEnvironmentVariable(entry.Key);
                Environment.SetEnvironmentVariable(entry.Key, entry.Value);
            }
            try
            {
                body();
            }
            finally
            {
                foreach (KeyValuePair<string, string?> entry in prior)
                    Environment.SetEnvironmentVariable(entry.Key, entry.Value);
            }
        }

        private static LoggingModule CreateLogging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }

        private sealed class InspectableArgs : ClaudeCodeRuntime
        {
            public InspectableArgs() : base(CreateLogging())
            {
            }

            public List<string> Build() => BuildArguments(Path.GetTempPath(), "prompt", null, null, null);
        }
    }
}
