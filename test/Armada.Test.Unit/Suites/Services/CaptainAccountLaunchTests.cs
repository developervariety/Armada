namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using Armada.Runtimes;
    using Armada.Test.Common;
    using SyslogLogging;

    /// <summary>
    /// Behavioural coverage of per-account captain logins: the environment each runtime process actually launches
    /// with, validation of account bindings, login readiness, and per-account usage collection.
    /// </summary>
    public sealed class CaptainAccountLaunchTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Captain Account Launch";

        private const string _OutputVariable = "ARMADA_ACCOUNT_ENV_OUT";

        private static LoggingModule Logging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }

        private static string TempDirectory(string label)
        {
            string path = Path.Combine(Path.GetTempPath(), "armada_account_" + label + "_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        private static string WriteScript(string directory, string name, string body)
        {
            string path = Path.Combine(directory, name);
            File.WriteAllText(path, "#!/bin/sh\n" + body);
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return path;
        }

        private static string LoggedInHome(AgentRuntimeEnum runtime)
        {
            string home = TempDirectory("home");
            if (runtime == AgentRuntimeEnum.ClaudeCode) File.WriteAllText(Path.Combine(home, ".credentials.json"), "{}");
            if (runtime == AgentRuntimeEnum.Codex) File.WriteAllText(Path.Combine(home, "auth.json"), "{}");
            if (runtime == AgentRuntimeEnum.OpenCode)
            {
                Directory.CreateDirectory(Path.Combine(home, "opencode"));
                File.WriteAllText(Path.Combine(home, "opencode", "auth.json"), "{}");
            }
            return home;
        }

        /// <summary>Launch a real runtime process through StartAsync and return the environment it received.</summary>
        private static async Task<Dictionary<string, string>> LaunchAndCaptureAsync(BaseAgentRuntime runtime, Captain captain, CaptainLaunchIsolationPlan? plan, string scratch)
        {
            string output = Path.Combine(scratch, "env.txt");
            string workingDirectory = Path.Combine(scratch, "dock");
            Directory.CreateDirectory(workingDirectory);
            int pid = await runtime.StartAsync(workingDirectory, "account launch probe",
                environment: new Dictionary<string, string> { [_OutputVariable] = output },
                model: captain.Model, captain: captain, isolationPlan: plan).ConfigureAwait(false);
            if (pid <= 0) throw new Exception("Runtime did not start");
            DateTime deadline = DateTime.UtcNow.AddSeconds(20);
            while (!File.Exists(output) && DateTime.UtcNow < deadline) await Task.Delay(50).ConfigureAwait(false);
            if (!File.Exists(output)) throw new Exception("Launched runtime wrote no environment");
            Dictionary<string, string> environment = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string line in await File.ReadAllLinesAsync(output).ConfigureAwait(false))
            {
                int eq = line.IndexOf('=');
                if (eq > 0) environment[line.Substring(0, eq)] = line.Substring(eq + 1);
            }
            return environment;
        }

        private static string EnvironmentProbeBody()
        {
            // Write atomically so the reader never sees a partial file.
            return "env > \"$" + _OutputVariable + ".tmp\" && mv \"$" + _OutputVariable + ".tmp\" \"$" + _OutputVariable + "\"\n";
        }

        private static BaseAgentRuntime CreateRuntime(AgentRuntimeEnum runtime, string probe, ModelProvidersSettings? providers = null)
        {
            switch (runtime)
            {
                case AgentRuntimeEnum.ClaudeCode: return new ClaudeCodeRuntime(Logging()) { ExecutablePath = probe };
                case AgentRuntimeEnum.Codex: return new CodexRuntime(Logging(), providers) { ExecutablePath = probe };
                case AgentRuntimeEnum.Cursor: return new CursorRuntime(Logging()) { ExecutablePath = probe };
                case AgentRuntimeEnum.OpenCode: return new OpenCodeRuntime(Logging());
                default: throw new ArgumentOutOfRangeException(nameof(runtime));
            }
        }

        private async Task<Dictionary<string, string>> LaunchRuntimeAsync(AgentRuntimeEnum runtimeType, UsageAccountSettings? account, string scratch, Dictionary<string, string?>? serverEnvironment = null)
        {
            string probe = WriteScript(scratch, "probe", EnvironmentProbeBody());
            string? previousOpenCode = Environment.GetEnvironmentVariable("ARMADA_TEST_OPENCODE");
            string? previousCursor = Environment.GetEnvironmentVariable("ARMADA_TEST_CURSOR_AGENT");
            try
            {
                if (runtimeType == AgentRuntimeEnum.OpenCode) Environment.SetEnvironmentVariable("ARMADA_TEST_OPENCODE", probe);
                if (runtimeType == AgentRuntimeEnum.Cursor) Environment.SetEnvironmentVariable("ARMADA_TEST_CURSOR_AGENT", null);
                BaseAgentRuntime runtime = CreateRuntime(runtimeType, probe);
                Captain captain = new Captain("account-probe-" + runtimeType, runtimeType) { Model = "account-probe-model" };
                CaptainLaunchIsolationPlan plan = CaptainLaunchIsolationPlanner.ApplyAccount(new CaptainLaunchIsolationPlan(), captain, account,
                    name => serverEnvironment != null && serverEnvironment.TryGetValue(name, out string? value) ? value : null);
                return await LaunchAndCaptureAsync(runtime, captain, plan.IsEmpty ? null : plan, scratch).ConfigureAwait(false);
            }
            finally
            {
                Environment.SetEnvironmentVariable("ARMADA_TEST_OPENCODE", previousOpenCode);
                Environment.SetEnvironmentVariable("ARMADA_TEST_CURSOR_AGENT", previousCursor);
            }
        }

        private void AssertInheritedUnchanged(Dictionary<string, string> launched, string variable)
        {
            string? inherited = Environment.GetEnvironmentVariable(variable);
            if (inherited == null) AssertFalse(launched.ContainsKey(variable), variable + " must not be introduced for a captain without an account login");
            else AssertEqual(inherited, launched[variable], variable + " must stay as the Admiral inherited it");
        }

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            Dictionary<AgentRuntimeEnum, string> switches = new Dictionary<AgentRuntimeEnum, string>
            {
                [AgentRuntimeEnum.ClaudeCode] = "CLAUDE_CONFIG_DIR",
                [AgentRuntimeEnum.Codex] = "CODEX_HOME",
                [AgentRuntimeEnum.OpenCode] = "XDG_DATA_HOME"
            };

            await RunTest("POSIX shell skip applies only on Windows", () =>
            {
                AssertNull(PosixShellSkipReason(false), "a POSIX host must run the launch probes, never skip them");
                AssertNotNull(PosixShellSkipReason(true), "a Windows host must record a named skip");
                AssertEqual(OperatingSystem.IsWindows(), PosixShellSkipReason(OperatingSystem.IsWindows()) != null, "the skip follows the current host");
            });

            string? posixSkip = PosixShellSkipReason(OperatingSystem.IsWindows());
            if (posixSkip != null)
            {
                // The launch probes are POSIX shell scripts. Record a named, counted skip rather than a failure.
                SkipTest("launch_environment_tests_require_posix_shell", posixSkip);
            }
            else
            {
                foreach (KeyValuePair<AgentRuntimeEnum, string> pair in switches)
                {
                    AgentRuntimeEnum runtimeType = pair.Key;
                    string variable = pair.Value;
                    await RunTest(runtimeType + " captain on an account launches with " + variable + " set to the account home", async () =>
                    {
                        string scratch = TempDirectory("launch");
                        string home = LoggedInHome(runtimeType);
                        try
                        {
                            UsageAccountSettings account = new UsageAccountSettings { Id = "second", Runtime = runtimeType, HomeDirectory = home };
                            Dictionary<string, string> launched = await LaunchRuntimeAsync(runtimeType, account, scratch).ConfigureAwait(false);
                            AssertEqual(home, launched[variable]);
                            AssertTrue(launched.ContainsKey("HOME"), "the account switch must not remove HOME");
                            AssertFalse(String.Equals(home, launched["HOME"], StringComparison.Ordinal), "the account switch must not replace HOME");
                        }
                        finally { Directory.Delete(scratch, true); Directory.Delete(home, true); }
                    });

                    await RunTest(runtimeType + " captain with no account, or an account with no home, launches exactly as before", async () =>
                    {
                        string scratch = TempDirectory("launch");
                        try
                        {
                            Dictionary<string, string> none = await LaunchRuntimeAsync(runtimeType, null, scratch).ConfigureAwait(false);
                            AssertInheritedUnchanged(none, variable);
                            File.Delete(Path.Combine(scratch, "env.txt"));
                            UsageAccountSettings usageOnly = new UsageAccountSettings { Id = "usage-only", Runtime = runtimeType };
                            Dictionary<string, string> homeless = await LaunchRuntimeAsync(runtimeType, usageOnly, scratch).ConfigureAwait(false);
                            AssertInheritedUnchanged(homeless, variable);
                            AssertEqual(none["HOME"], homeless["HOME"]);
                        }
                        finally { Directory.Delete(scratch, true); }
                    });
                }

                await RunTest("Cursor captain on an account launches with CURSOR_API_KEY read from the named variable and keeps HOME", async () =>
                {
                    string scratch = TempDirectory("launch");
                    try
                    {
                        UsageAccountSettings account = new UsageAccountSettings { Id = "cursor-second", Runtime = AgentRuntimeEnum.Cursor, LaunchCredentialEnv = "CURSOR_SECOND_ACCOUNT_KEY" };
                        Dictionary<string, string> launched = await LaunchRuntimeAsync(AgentRuntimeEnum.Cursor, account, scratch,
                            new Dictionary<string, string?> { ["CURSOR_SECOND_ACCOUNT_KEY"] = "cursor-key-not-a-real-credential" }).ConfigureAwait(false);
                        AssertEqual("cursor-key-not-a-real-credential", launched["CURSOR_API_KEY"]);
                        AssertEqual(Environment.GetEnvironmentVariable("HOME"), launched["HOME"], "Cursor keeps the Admiral HOME so git, gh and ssh configuration stay visible");
                        AssertFalse(System.Text.Json.JsonSerializer.Serialize(account).Contains("cursor-key-not-a-real-credential"), "settings hold only the variable name");
                    }
                    finally { Directory.Delete(scratch, true); }
                });

                await RunTest("Cursor captain with no account launches exactly as before", async () =>
                {
                    string scratch = TempDirectory("launch");
                    try
                    {
                        Dictionary<string, string> launched = await LaunchRuntimeAsync(AgentRuntimeEnum.Cursor, null, scratch).ConfigureAwait(false);
                        AssertInheritedUnchanged(launched, "CURSOR_API_KEY");
                    }
                    finally { Directory.Delete(scratch, true); }
                });

                await RunTest("Codex provider profile for an account captain is written into the account CODEX_HOME", async () =>
                {
                    string scratch = TempDirectory("launch");
                    string home = LoggedInHome(AgentRuntimeEnum.Codex);
                    try
                    {
                        ModelProvidersSettings registry = new ModelProvidersSettings();
                        registry.Providers["account-provider"] = new ModelProviderSettings { Name = "account-provider", BaseUrl = "https://account-provider.example", ApiKeyEnv = "ACCOUNT_PROVIDER_TEST_KEY" };
                        Environment.SetEnvironmentVariable("ACCOUNT_PROVIDER_TEST_KEY", "provider-key-not-a-real-credential");
                        string probe = WriteScript(scratch, "probe", EnvironmentProbeBody());
                        BaseAgentRuntime runtime = CreateRuntime(AgentRuntimeEnum.Codex, probe, registry);
                        Captain captain = new Captain("codex-provider-account", AgentRuntimeEnum.Codex) { Model = "account-provider/model-x" };
                        UsageAccountSettings account = new UsageAccountSettings { Id = "codex-second", Runtime = AgentRuntimeEnum.Codex, HomeDirectory = home };
                        CaptainLaunchIsolationPlan plan = CaptainLaunchIsolationPlanner.ApplyAccount(new CaptainLaunchIsolationPlan(), captain, account);
                        Dictionary<string, string> launched = await LaunchAndCaptureAsync(runtime, captain, plan, scratch).ConfigureAwait(false);
                        AssertEqual(home, launched["CODEX_HOME"]);
                        AssertTrue(File.Exists(Path.Combine(home, "account-provider.config.toml")), "the provider profile must be in the account home, where Codex reads it");
                    }
                    finally
                    {
                        Environment.SetEnvironmentVariable("ACCOUNT_PROVIDER_TEST_KEY", null);
                        Directory.Delete(scratch, true);
                        Directory.Delete(home, true);
                    }
                });

                await RunTest("Two Codex accounts are measured through their own CODEX_HOME and report separate windows", async () =>
                {
                    string scratch = TempDirectory("codex-usage");
                    string first = TempDirectory("codex-first");
                    string second = TempDirectory("codex-second");
                    try
                    {
                        long reset = DateTimeOffset.UtcNow.AddDays(2).ToUnixTimeSeconds();
                        File.WriteAllText(Path.Combine(first, "reply.json"), "{\"id\":2,\"result\":{\"rateLimits\":{\"limitId\":\"codex\",\"primary\":{\"usedPercent\":30,\"resetsAt\":" + reset + "}}}}");
                        File.WriteAllText(Path.Combine(second, "reply.json"), "{\"id\":2,\"result\":{\"rateLimits\":{\"limitId\":\"codex\",\"primary\":{\"usedPercent\":80,\"resetsAt\":" + reset + "}}}}");
                        string fake = WriteScript(scratch, "codex", "read line\necho '{\"id\":1,\"result\":{}}'\nread line\nread line\ncat \"$CODEX_HOME/reply.json\"\necho\n");
                        UsageAccountSettings firstAccount = new UsageAccountSettings { Id = "codex-a", Collector = "Codex", Runtime = AgentRuntimeEnum.Codex, HomeDirectory = first };
                        UsageAccountSettings secondAccount = new UsageAccountSettings { Id = "codex-b", Collector = "Codex", Runtime = AgentRuntimeEnum.Codex, HomeDirectory = second };
                        ProviderUsageSnapshot a = await CodexUsageCollector.CollectAsync(firstAccount, fake, default).ConfigureAwait(false);
                        ProviderUsageSnapshot b = await CodexUsageCollector.CollectAsync(secondAccount, fake, default).ConfigureAwait(false);
                        AssertEqual(70d, a.Windows.Single().RemainingPercent!.Value);
                        AssertEqual(20d, b.Windows.Single().RemainingPercent!.Value);
                    }
                    finally { Directory.Delete(scratch, true); Directory.Delete(first, true); Directory.Delete(second, true); }
                });
            }

            await RunTest("Codex collector without a home keeps the server user's login", () =>
            {
                ProcessStartInfo info = CodexUsageCollector.BuildStartInfo(new UsageAccountSettings { Id = "shared", Collector = "Codex" }, "codex");
                string? inherited = Environment.GetEnvironmentVariable("CODEX_HOME");
                if (inherited == null) AssertFalse(info.Environment.ContainsKey("CODEX_HOME"));
                else AssertEqual(inherited, info.Environment["CODEX_HOME"]);
            });

            await RunTest("A missing login blocks launch with a named reason and never falls back to the shared login", () =>
            {
                string home = TempDirectory("empty-home");
                try
                {
                    Captain captain = new Captain("claude-account", AgentRuntimeEnum.ClaudeCode);
                    AssertLaunchRefused(captain, new UsageAccountSettings { Id = "no-login", Runtime = AgentRuntimeEnum.ClaudeCode, HomeDirectory = home }, CaptainAccountLaunch.ReasonLoginMissing);
                    AssertLaunchRefused(captain, new UsageAccountSettings { Id = "no-home", Runtime = AgentRuntimeEnum.ClaudeCode, HomeDirectory = Path.Combine(home, "absent") }, CaptainAccountLaunch.ReasonHomeMissing);
                    AssertLaunchRefused(new Captain("cursor-account", AgentRuntimeEnum.Cursor), new UsageAccountSettings { Id = "no-key", Runtime = AgentRuntimeEnum.Cursor, LaunchCredentialEnv = "ARMADA_ACCOUNT_KEY_NOT_SET_ANYWHERE" }, CaptainAccountLaunch.ReasonCredentialUnavailable);
                    AssertLaunchRefused(new Captain("codex-captain", AgentRuntimeEnum.Codex), new UsageAccountSettings { Id = "wrong-runtime", Runtime = AgentRuntimeEnum.ClaudeCode, HomeDirectory = home }, CaptainAccountLaunch.ReasonRuntimeMismatch);
                    Captain provider = new Captain("provider-captain", AgentRuntimeEnum.ClaudeCode) { ApiKey = "k", ApiBaseUrl = "https://provider.example" };
                    File.WriteAllText(Path.Combine(home, ".credentials.json"), "{}");
                    AssertLaunchRefused(provider, new UsageAccountSettings { Id = "provider", Runtime = AgentRuntimeEnum.ClaudeCode, HomeDirectory = home }, CaptainAccountLaunch.ReasonProviderCaptain);
                }
                finally { Directory.Delete(home, true); }
            });

            await RunTest("A logged-out account reads Exhausted with a named reason in status and blocks routing", () =>
            {
                string home = TempDirectory("logged-out");
                try
                {
                    UsageAccountSettings loggedOut = new UsageAccountSettings { Id = "logged-out", Runtime = AgentRuntimeEnum.Codex, HomeDirectory = home, CaptainIds = new List<string> { "first" } };
                    UsageAccountSettings shared = new UsageAccountSettings { Id = "shared", CaptainIds = new List<string> { "second" } };
                    UsageRoutingSettings policy = new UsageRoutingSettings
                    {
                        Enabled = true, Accounts = new List<UsageAccountSettings> { loggedOut, shared },
                        PersonaRoutes = new Dictionary<string, List<UsageRouteSettings>> { ["Worker"] = new List<UsageRouteSettings> { new UsageRouteSettings { AccountId = "logged-out" }, new UsageRouteSettings { AccountId = "shared" } } }
                    };
                    UsageRoutingService service = new UsageRoutingService();
                    ProviderUsageStatus status = service.GetStatus(loggedOut, null, DateTime.UtcNow);
                    AssertEqual("Exhausted", status.State);
                    AssertEqual(CaptainAccountLaunch.ReasonLoginMissing, status.Reason);
                    AssertEqual("Codex", status.Runtime);
                    UsageRoutingDecision decision = service.Select(policy, new Mission { Persona = "Worker" },
                        new List<Captain> { new Captain("first") { Id = "first" }, new Captain("second") { Id = "second" } }, Array.Empty<string>(), DateTime.UtcNow);
                    AssertEqual("second", decision.Candidates.Single().Id);
                    File.WriteAllText(Path.Combine(home, "auth.json"), "{}");
                    AssertFalse(service.GetStatus(loggedOut, null, DateTime.UtcNow).Reason == CaptainAccountLaunch.ReasonLoginMissing, "a login added in the home clears the block");
                }
                finally { Directory.Delete(home, true); }
            });

            await RunTest("An account exhaustion mark blocks every captain on it until its retry time", () =>
            {
                UsageAccountSettings account = new UsageAccountSettings { Id = "held", CaptainIds = new List<string> { "a", "b" } };
                UsageRoutingSettings policy = new UsageRoutingSettings
                {
                    Enabled = true, Accounts = new List<UsageAccountSettings> { account },
                    PersonaRoutes = new Dictionary<string, List<UsageRouteSettings>> { ["Worker"] = new List<UsageRouteSettings> { new UsageRouteSettings { AccountId = "held" } } }
                };
                UsageRoutingService service = new UsageRoutingService();
                List<Captain> both = new List<Captain> { new Captain("a") { Id = "a" }, new Captain("b") { Id = "b" } };
                AssertEqual(2, service.Select(policy, new Mission { Persona = "Worker" }, both, Array.Empty<string>(), DateTime.UtcNow).Candidates.Count);
                DateTime until = DateTime.UtcNow.AddMinutes(30);
                service.MarkAccountExhausted("held", until);
                AssertEqual(0, service.Select(policy, new Mission { Persona = "Worker" }, both, Array.Empty<string>(), DateTime.UtcNow).Candidates.Count);
                AssertEqual("account_provider_failure", service.GetStatus(account, null, DateTime.UtcNow).Reason);
                AssertEqual(2, service.Select(policy, new Mission { Persona = "Worker" }, both, Array.Empty<string>(), until.AddSeconds(1)).Candidates.Count, "the hold ends at the retry time");
            });

            await RunTest("Validation rejects account runtimes that do not match their captains or collectors", () =>
            {
                Captain claude = new Captain("claude", AgentRuntimeEnum.ClaudeCode);
                Captain codex = new Captain("codex", AgentRuntimeEnum.Codex);
                UsageRoutingSettings policy = new UsageRoutingSettings
                {
                    Accounts = new List<UsageAccountSettings> { new UsageAccountSettings { Id = "codex-account", Runtime = AgentRuntimeEnum.Codex, HomeDirectory = Path.GetTempPath(), CaptainIds = new List<string> { codex.Id, claude.Id } } }
                };
                UsageRoutingService.Validate(policy);
                AssertThrows<ArgumentException>(() => CaptainAccountLaunch.ValidateCaptainBindings(policy, new List<Captain> { claude, codex }));
                policy.Accounts[0].CaptainIds.Remove(claude.Id);
                CaptainAccountLaunch.ValidateCaptainBindings(policy, new List<Captain> { claude, codex });
                codex.ApiBaseUrl = "https://provider.example";
                AssertThrows<ArgumentException>(() => CaptainAccountLaunch.ValidateCaptainBindings(policy, new List<Captain> { codex }), "a captain with its own endpoint cannot take an account login");

                AssertThrows<ArgumentException>(() => UsageRoutingService.Validate(Single(new UsageAccountSettings { Id = "x", Runtime = AgentRuntimeEnum.Codex, Collector = "Claude" })), "collector must measure the account runtime");
                AssertThrows<ArgumentException>(() => UsageRoutingService.Validate(Single(new UsageAccountSettings { Id = "x", Runtime = AgentRuntimeEnum.Cursor, HomeDirectory = Path.GetTempPath() })), "Cursor must not replace HOME");
                AssertThrows<ArgumentException>(() => UsageRoutingService.Validate(Single(new UsageAccountSettings { Id = "x", Runtime = AgentRuntimeEnum.Cursor, LaunchCredentialEnv = "sk-not a variable name" })), "a key value is not a variable name");
                AssertThrows<ArgumentException>(() => UsageRoutingService.Validate(Single(new UsageAccountSettings { Id = "x", Runtime = AgentRuntimeEnum.Codex, HomeDirectory = "relative/home" })), "home must be absolute");
                AssertThrows<ArgumentException>(() => UsageRoutingService.Validate(Single(new UsageAccountSettings { Id = "x", HomeDirectory = Path.GetTempPath() })), "a home needs a runtime");
                AssertThrows<ArgumentException>(() => UsageRoutingService.Validate(Single(new UsageAccountSettings { Id = "x", Runtime = AgentRuntimeEnum.Gemini, HomeDirectory = Path.GetTempPath() })), "Gemini is not supported");
            });

            await RunTest("Claude usage collection reads the account home login when no reference is set", () =>
            {
                UsageAccountSettings account = new UsageAccountSettings { Id = "claude-second", Collector = "Claude", Runtime = AgentRuntimeEnum.ClaudeCode, HomeDirectory = Path.Combine(Path.GetTempPath(), "claude-second-home") };
                AssertEqual(Path.Combine(account.HomeDirectory, ".credentials.json"), CaptainAccountLaunch.LoginFilePath(account));
                UsageAccountSettings go = new UsageAccountSettings { Id = "go", Collector = "OpenCodeGo", Runtime = AgentRuntimeEnum.OpenCode, HomeDirectory = Path.Combine(Path.GetTempPath(), "go-home") };
                AssertEqual(Path.Combine(go.HomeDirectory, "opencode", "auth.json"), CaptainAccountLaunch.LoginFilePath(go));
            });
        }

        /// <summary>The one decision for whether shell-stub tests run: they need a POSIX shell, so only Windows skips.</summary>
        internal static string? PosixShellSkipReason(bool isWindows)
        {
            return isWindows ? "Stub runtimes are POSIX shell scripts; run these tests on Linux or macOS." : null;
        }

        private static UsageRoutingSettings Single(UsageAccountSettings account)
        {
            return new UsageRoutingSettings { Accounts = new List<UsageAccountSettings> { account } };
        }

        private void AssertLaunchRefused(Captain captain, UsageAccountSettings account, string code)
        {
            try
            {
                CaptainLaunchIsolationPlanner.ApplyAccount(new CaptainLaunchIsolationPlan(), captain, account);
            }
            catch (CaptainAccountLaunchException ex)
            {
                AssertEqual(code, ex.Code);
                return;
            }
            throw new Exception("Expected launch refusal " + code + " for account " + account.Id);
        }
    }
}
