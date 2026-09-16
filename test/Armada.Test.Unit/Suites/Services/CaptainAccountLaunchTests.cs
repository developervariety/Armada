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

                await RunTest("Cursor captain on an account launches with CURSOR_API_KEY read from its key file and keeps HOME", async () =>
                {
                    string scratch = TempDirectory("launch");
                    try
                    {
                        string keyFile = Path.Combine(scratch, "accounts", "cursor-file", AccountLoginPaths.CursorKeyFileName);
                        Directory.CreateDirectory(Path.GetDirectoryName(keyFile)!);
                        File.WriteAllText(keyFile, "cursor-file-key-not-a-real-credential\n");
                        UsageAccountSettings account = new UsageAccountSettings { Id = "cursor-file", Runtime = AgentRuntimeEnum.Cursor, LaunchCredentialFile = keyFile };
                        Dictionary<string, string> launched = await LaunchRuntimeAsync(AgentRuntimeEnum.Cursor, account, scratch).ConfigureAwait(false);
                        AssertEqual("cursor-file-key-not-a-real-credential", launched["CURSOR_API_KEY"]);
                        AssertEqual(Environment.GetEnvironmentVariable("HOME"), launched["HOME"], "Cursor keeps the Admiral HOME");
                        AssertFalse(System.Text.Json.JsonSerializer.Serialize(account).Contains("cursor-file-key-not-a-real-credential"), "settings hold only the file path");
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

            string? probeSkip = PosixShellSkipReason(OperatingSystem.IsWindows());
            if (probeSkip != null)
            {
                SkipTest("login_probe_tests_require_posix_shell", probeSkip);
            }
            else
            {
                await RunTest("Claude login probe runs auth status in the account home: logged in, logged out, hang and missing CLI", async () =>
                {
                    string scratch = TempDirectory("probe-claude");
                    string home = LoggedInHome(AgentRuntimeEnum.ClaudeCode);
                    try
                    {
                        UsageAccountSettings account = new UsageAccountSettings { Id = "claude-probe", Runtime = AgentRuntimeEnum.ClaudeCode, HomeDirectory = home };
                        string loggedIn = WriteScript(scratch, "claude-in", "echo \"$CLAUDE_CONFIG_DIR $*\" > \"$CLAUDE_CONFIG_DIR/probe-args\"\necho '{\"loggedIn\":true,\"authMethod\":\"claude.ai\"}'\nexit 0\n");
                        string loggedOut = WriteScript(scratch, "claude-out", "echo '{\"loggedIn\":false,\"authMethod\":\"none\",\"token\":\"probe-secret-token\"}'\nexit 1\n");
                        string hang = WriteScript(scratch, "claude-hang", "sleep 30\n");
                        AssertNull(await AccountLoginProbe.RunAsync(account, loggedIn, TimeSpan.FromSeconds(10)).ConfigureAwait(false), "logged in");
                        AssertEqual(home + " auth status --json", File.ReadAllText(Path.Combine(home, "probe-args")).Trim(), "the probe runs auth status --json with CLAUDE_CONFIG_DIR set to the account home");
                        string? outReason = await AccountLoginProbe.RunAsync(account, loggedOut, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                        AssertEqual(AccountLoginProbe.ReasonLoginExpired, outReason);
                        AssertFalse(outReason!.Contains("probe-secret-token"), "command output never reaches the result");
                        Stopwatch watch = Stopwatch.StartNew();
                        AssertEqual(AccountLoginProbe.ReasonProbeTimeout, await AccountLoginProbe.RunAsync(account, hang, TimeSpan.FromSeconds(1)).ConfigureAwait(false));
                        AssertTrue(watch.Elapsed < TimeSpan.FromSeconds(10), "a hanging CLI is bounded by the timeout, took " + watch.Elapsed);
                        AssertEqual(AccountLoginProbe.ReasonProbeUnavailable, await AccountLoginProbe.RunAsync(account, Path.Combine(scratch, "absent-claude"), TimeSpan.FromSeconds(10)).ConfigureAwait(false));
                    }
                    finally { Directory.Delete(scratch, true); Directory.Delete(home, true); }
                });

                await RunTest("Codex login probe runs login status in the account CODEX_HOME: logged in, logged out, hang and missing CLI", async () =>
                {
                    string scratch = TempDirectory("probe-codex");
                    string home = LoggedInHome(AgentRuntimeEnum.Codex);
                    try
                    {
                        UsageAccountSettings account = new UsageAccountSettings { Id = "codex-probe", Runtime = AgentRuntimeEnum.Codex, HomeDirectory = home };
                        string loggedIn = WriteScript(scratch, "codex-in", "echo \"$CODEX_HOME $*\" > \"$CODEX_HOME/probe-args\"\necho 'Logged in using ChatGPT'\nexit 0\n");
                        string loggedOut = WriteScript(scratch, "codex-out", "echo 'Not logged in' >&2\nexit 1\n");
                        string broken = WriteScript(scratch, "codex-broken", "echo 'unexpected failure'\nexit 2\n");
                        string hang = WriteScript(scratch, "codex-hang", "sleep 30\n");
                        AssertNull(await AccountLoginProbe.RunAsync(account, loggedIn, TimeSpan.FromSeconds(10)).ConfigureAwait(false), "logged in");
                        AssertEqual(home + " login status", File.ReadAllText(Path.Combine(home, "probe-args")).Trim());
                        AssertEqual(AccountLoginProbe.ReasonLoginExpired, await AccountLoginProbe.RunAsync(account, loggedOut, TimeSpan.FromSeconds(10)).ConfigureAwait(false));
                        AssertEqual(AccountLoginProbe.ReasonProbeFailed, await AccountLoginProbe.RunAsync(account, broken, TimeSpan.FromSeconds(10)).ConfigureAwait(false));
                        AssertEqual(AccountLoginProbe.ReasonProbeTimeout, await AccountLoginProbe.RunAsync(account, hang, TimeSpan.FromSeconds(1)).ConfigureAwait(false));
                        AssertEqual(AccountLoginProbe.ReasonProbeUnavailable, await AccountLoginProbe.RunAsync(account, Path.Combine(scratch, "absent-codex"), TimeSpan.FromSeconds(10)).ConfigureAwait(false));
                    }
                    finally { Directory.Delete(scratch, true); Directory.Delete(home, true); }
                });

                await RunTest("Routing status never waits for a hanging login probe and caches each result for the interval", async () =>
                {
                    string scratch = TempDirectory("probe-cache");
                    string home = LoggedInHome(AgentRuntimeEnum.Codex);
                    try
                    {
                        string counter = Path.Combine(scratch, "calls");
                        string hang = WriteScript(scratch, "codex-hang", "echo x >> \"" + counter + "\"\nsleep 30\n");
                        string loggedOut = WriteScript(scratch, "codex-out", "echo x >> \"" + counter + "\"\necho 'Not logged in'\nexit 1\n");
                        UsageAccountSettings account = new UsageAccountSettings { Id = "cached", Runtime = AgentRuntimeEnum.Codex, HomeDirectory = home, CaptainIds = new List<string> { "a" } };
                        UsageRoutingSettings policy = new UsageRoutingSettings { LoginProbeTimeoutSeconds = 1, LoginProbeIntervalMinutes = 10, Accounts = new List<UsageAccountSettings> { account } };
                        UsageRoutingService service = new UsageRoutingService();
                        await service.RefreshAsync(policy).ConfigureAwait(false);

                        service.LoginProbeExecutable = _ => hang;
                        Stopwatch watch = Stopwatch.StartNew();
                        ProviderUsageStatus pending = service.GetStatus(account, null, DateTime.UtcNow);
                        AssertTrue(watch.Elapsed < TimeSpan.FromMilliseconds(500), "status must not wait for the probe, took " + watch.Elapsed);
                        AssertFalse(pending.Reason == AccountLoginProbe.ReasonProbeTimeout, "the probe has not finished yet");
                        AssertEqual(AccountLoginProbe.ReasonProbeTimeout, await WaitForReasonAsync(service, account, AccountLoginProbe.ReasonProbeTimeout).ConfigureAwait(false));
                        ProviderUsageStatus timedOut = service.GetStatus(account, null, DateTime.UtcNow);
                        AssertEqual("Exhausted", timedOut.State);
                        AssertNotNull(timedOut.LoginCheckedUtc);

                        service.LoginProbeExecutable = _ => loggedOut;
                        int callsBefore = File.ReadAllLines(counter).Length;
                        service.GetStatus(account, null, DateTime.UtcNow);
                        await Task.Delay(500).ConfigureAwait(false);
                        AssertEqual(callsBefore, File.ReadAllLines(counter).Length, "a fresh result is reused within the interval");
                        AssertEqual(AccountLoginProbe.ReasonLoginExpired, await WaitForReasonAsync(service, account, AccountLoginProbe.ReasonLoginExpired, DateTime.UtcNow.AddMinutes(11)).ConfigureAwait(false), "after the interval the probe runs again");
                        AssertEqual(callsBefore + 1, File.ReadAllLines(counter).Length);
                        AssertFalse(System.Text.Json.JsonSerializer.Serialize(service.GetStatus(account, null, DateTime.UtcNow.AddMinutes(11))).Contains("Not logged in"), "probe output never reaches status");
                    }
                    finally { Directory.Delete(scratch, true); Directory.Delete(home, true); }
                });

                await RunTest("An expired account login names account_login_expired in status and in the routing decision that blocks assignment", async () =>
                {
                    string scratch = TempDirectory("probe-expired");
                    string home = LoggedInHome(AgentRuntimeEnum.Codex);
                    try
                    {
                        string loggedOut = WriteScript(scratch, "codex-out", "echo 'Not logged in'\nexit 1\n");
                        UsageAccountSettings expired = new UsageAccountSettings { Id = "expired", Runtime = AgentRuntimeEnum.Codex, HomeDirectory = home, CaptainIds = new List<string> { "first" } };
                        UsageAccountSettings shared = new UsageAccountSettings { Id = "shared", CaptainIds = new List<string> { "second" } };
                        UsageRoutingSettings onlyExpired = new UsageRoutingSettings
                        {
                            Enabled = true, Accounts = new List<UsageAccountSettings> { expired, shared },
                            PersonaRoutes = new Dictionary<string, List<UsageRouteSettings>> { ["Worker"] = new List<UsageRouteSettings> { new UsageRouteSettings { AccountId = "expired" } } }
                        };
                        UsageRoutingService service = new UsageRoutingService { LoginProbeExecutable = _ => loggedOut };
                        List<Captain> captains = new List<Captain> { new Captain("first") { Id = "first" }, new Captain("second") { Id = "second" } };

                        // The login file is present, so only the runtime's own status command can see the expiry.
                        AssertNull(CaptainAccountLaunch.CheckReadiness(expired), "the file pre-filter passes");
                        AssertEqual(AccountLoginProbe.ReasonLoginExpired, await WaitForReasonAsync(service, expired, AccountLoginProbe.ReasonLoginExpired).ConfigureAwait(false));
                        ProviderUsageStatus status = service.GetStatus(expired, null, DateTime.UtcNow);
                        AssertEqual("Exhausted", status.State);
                        AssertEqual(AccountLoginProbe.ReasonLoginExpired, status.Reason);

                        UsageRoutingDecision blocked = service.Select(onlyExpired, new Mission { Persona = "Worker" }, captains, Array.Empty<string>(), DateTime.UtcNow);
                        AssertEqual(0, blocked.Candidates.Count, "no captain on the expired account receives the mission");
                        AssertEqual(AccountLoginProbe.ReasonLoginExpired, blocked.Reason, "the waiting mission names the account login problem");

                        onlyExpired.PersonaRoutes["Worker"].Add(new UsageRouteSettings { AccountId = "shared" });
                        UsageRoutingDecision fallback = service.Select(onlyExpired, new Mission { Persona = "Worker" }, captains, Array.Empty<string>(), DateTime.UtcNow);
                        AssertEqual("second", fallback.Candidates.Single().Id, "an approved fallback account still takes the mission");
                    }
                    finally { Directory.Delete(scratch, true); Directory.Delete(home, true); }
                });
            }

            await RunTest("OpenCode and Cursor accounts keep the file or variable check because their status commands cannot verify one account", () =>
            {
                AssertFalse(AccountLoginProbe.HasStatusCommand(AgentRuntimeEnum.OpenCode));
                AssertFalse(AccountLoginProbe.HasStatusCommand(AgentRuntimeEnum.Cursor));
                string home = LoggedInHome(AgentRuntimeEnum.OpenCode);
                try
                {
                    UsageRoutingService service = new UsageRoutingService { LoginProbeExecutable = _ => throw new InvalidOperationException("OpenCode must not be probed") };
                    UsageAccountSettings account = new UsageAccountSettings { Id = "opencode", Runtime = AgentRuntimeEnum.OpenCode, HomeDirectory = home };
                    AssertNull(service.GetLoginProblem(account, DateTime.UtcNow));
                    AssertNull(service.GetLoginCheckedUtc("opencode"));
                }
                finally { Directory.Delete(home, true); }
            });

            await RunTest("Login probe settings are validated", () =>
            {
                AssertThrows<ArgumentException>(() => UsageRoutingService.Validate(new UsageRoutingSettings { LoginProbeIntervalMinutes = 0 }));
                AssertThrows<ArgumentException>(() => UsageRoutingService.Validate(new UsageRoutingSettings { LoginProbeTimeoutSeconds = 61 }));
                UsageRoutingService.Validate(new UsageRoutingSettings());
            });

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
                    AssertLaunchRefused(new Captain("cursor-account", AgentRuntimeEnum.Cursor), new UsageAccountSettings { Id = "no-key-file", Runtime = AgentRuntimeEnum.Cursor, LaunchCredentialFile = Path.Combine(home, "no-key-file", AccountLoginPaths.CursorKeyFileName) }, CaptainAccountLaunch.ReasonCredentialUnavailable);
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
                    // Never start a real runtime CLI from a unit test; this case covers the file pre-filter only.
                    UsageRoutingService service = new UsageRoutingService { LoginProbeExecutable = _ => Path.Combine(home, "no-such-cli") };
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

            await RunTest("A blocked routing decision names the account login reason, and a measured shortage keeps the generic reason", () =>
            {
                string home = TempDirectory("decision-reason");
                try
                {
                    UsageAccountSettings loggedOut = new UsageAccountSettings { Id = "logged-out", Runtime = AgentRuntimeEnum.ClaudeCode, HomeDirectory = home, CaptainIds = new List<string> { "first" } };
                    UsageAccountSettings drained = new UsageAccountSettings
                    {
                        Id = "drained", CaptainIds = new List<string> { "second" },
                        ManualSnapshot = new ProviderUsageSnapshot { ObservedUtc = DateTime.UtcNow, Source = "operator", Windows = new List<ProviderUsageWindow> { new ProviderUsageWindow { Name = "weekly", RemainingPercent = 0 } } }
                    };
                    List<Captain> captains = new List<Captain> { new Captain("first") { Id = "first" }, new Captain("second") { Id = "second" } };
                    UsageRoutingService service = new UsageRoutingService { LoginProbeExecutable = _ => Path.Combine(home, "no-such-cli") };

                    UsageRoutingSettings loginPolicy = new UsageRoutingSettings
                    {
                        Enabled = true, Accounts = new List<UsageAccountSettings> { loggedOut, drained },
                        PersonaRoutes = new Dictionary<string, List<UsageRouteSettings>> { ["Worker"] = new List<UsageRouteSettings> { new UsageRouteSettings { AccountId = "logged-out" } } }
                    };
                    UsageRoutingDecision login = service.Select(loginPolicy, new Mission { Persona = "Worker" }, captains, Array.Empty<string>(), DateTime.UtcNow);
                    AssertEqual(0, login.Candidates.Count);
                    AssertEqual(CaptainAccountLaunch.ReasonLoginMissing, login.Reason);

                    UsageRoutingSettings shortagePolicy = new UsageRoutingSettings
                    {
                        Enabled = true, Accounts = new List<UsageAccountSettings> { loggedOut, drained },
                        PersonaRoutes = new Dictionary<string, List<UsageRouteSettings>> { ["Worker"] = new List<UsageRouteSettings> { new UsageRouteSettings { AccountId = "drained" } } }
                    };
                    UsageRoutingDecision shortage = service.Select(shortagePolicy, new Mission { Persona = "Worker" }, captains, Array.Empty<string>(), DateTime.UtcNow);
                    AssertEqual(0, shortage.Candidates.Count);
                    AssertEqual("usage_reserve_exhaustion_or_account_capacity", shortage.Reason, "an exhausted allowance is not an account login problem");

                    service.MarkAccountExhausted("drained", DateTime.UtcNow.AddMinutes(30));
                    drained.ManualSnapshot!.Windows[0].RemainingPercent = 80;
                    AssertEqual("account_provider_failure", service.Select(shortagePolicy, new Mission { Persona = "Worker" }, captains, Array.Empty<string>(), DateTime.UtcNow).Reason, "a provider-failure hold names itself");
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

            await RunTest("Select with routing enabled but no route configured passes the legacy candidates through", () =>
            {
                // Smart Routing enabled with an empty configuration must not govern any persona: it returns
                // the candidates the legacy selector already approved, so enabling it fleet-wide is a safe
                // no-op until accounts and routes are added, never a blanket assignment block.
                UsageRoutingSettings policy = new UsageRoutingSettings { Enabled = true };
                UsageRoutingService service = new UsageRoutingService();
                List<Captain> both = new List<Captain> { new Captain("a") { Id = "a" }, new Captain("b") { Id = "b" } };
                UsageRoutingDecision decision = service.Select(policy, new Mission { Persona = "Worker" }, both, Array.Empty<string>(), DateTime.UtcNow);
                AssertEqual(2, decision.Candidates.Count, "an ungoverned persona keeps every legacy candidate");
                AssertEqual("v2_no_route_pass_through", decision.Reason);
                AssertFalse(decision.HasPersonaRoutes, "no persona route resolved");
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

        private static async Task<string?> WaitForReasonAsync(UsageRoutingService service, UsageAccountSettings account, string expected, DateTime? now = null)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(15);
            string? reason = null;
            while (DateTime.UtcNow < deadline)
            {
                reason = service.GetLoginProblem(account, now ?? DateTime.UtcNow);
                if (reason == expected) return reason;
                await Task.Delay(50).ConfigureAwait(false);
            }
            return reason;
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
