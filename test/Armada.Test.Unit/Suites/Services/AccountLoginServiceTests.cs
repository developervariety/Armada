namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.ComponentModel;
    using System.IO;
    using System.Linq;
    using System.Text.Json;
    using System.Text.Json.Nodes;
    using System.Threading;
    using System.Threading.Channels;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Server.Routes;
    using Armada.Test.Common;

    /// <summary>
    /// Behavioural coverage of dashboard account logins: server-derived account folders, device and paste-code logins
    /// driven through a fake login process, API-key storage, and the rule that keys, pasted codes, and other process
    /// output never reach the returned session, the log, or events.
    /// </summary>
    public sealed class AccountLoginServiceTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Account Login Service";

        private static string TempDirectory()
        {
            string path = Path.Combine(Path.GetTempPath(), "armada_login_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        private sealed class Harness : IDisposable
        {
            public string DataDirectory { get; } = TempDirectory();

            public FakeRunner Runner { get; } = new FakeRunner();

            public ConcurrentQueue<string> Logs { get; } = new ConcurrentQueue<string>();

            public ConcurrentQueue<string> Events { get; } = new ConcurrentQueue<string>();

            public ConcurrentQueue<string> Succeeded { get; } = new ConcurrentQueue<string>();

            public AccountLoginService Service { get; }

            public Harness()
            {
                Service = new AccountLoginService(DataDirectory, Runner, line => Logs.Enqueue(line), (type, message, id) => Events.Enqueue(type + " " + message + " " + id));
                Service.PromptTimeout = TimeSpan.FromSeconds(5);
                Service.OnLoginSucceeded = id => Succeeded.Enqueue(id);
            }

            public string Sinks()
            {
                return String.Join("\n", Logs) + "\n" + String.Join("\n", Events);
            }

            public void Dispose()
            {
                Service.Dispose();
                try { Directory.Delete(DataDirectory, true); }
                catch (IOException) { /* Temp cleanup only. */ }
            }
        }

        private sealed class FakeRunner : IAccountLoginProcessRunner
        {
            public List<AccountLoginProcessRequest> Requests { get; } = new List<AccountLoginProcessRequest>();

            public List<FakeProcess> Processes { get; } = new List<FakeProcess>();

            public Action<FakeProcess>? OnStart { get; set; }

            public Exception? Failure { get; set; }

            public IAccountLoginProcess Start(AccountLoginProcessRequest request)
            {
                if (Failure != null) throw Failure;
                FakeProcess process = new FakeProcess();
                lock (Requests)
                {
                    Requests.Add(request);
                    Processes.Add(process);
                }
                OnStart?.Invoke(process);
                return process;
            }
        }

        private sealed class FakeProcess : IAccountLoginProcess
        {
            private readonly Channel<string> _Output = Channel.CreateUnbounded<string>();
            private readonly TaskCompletionSource<bool> _Exit = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            public ConcurrentQueue<string> Inputs { get; } = new ConcurrentQueue<string>();

            public bool Killed { get; private set; }

            public int? ExitCode { get; private set; }

            public void Emit(string text) => _Output.Writer.TryWrite(text);

            public void Exit(int code)
            {
                ExitCode = code;
                _Output.Writer.TryComplete();
                _Exit.TrySetResult(true);
            }

            public async Task<string?> ReadOutputAsync(CancellationToken token = default)
            {
                while (await _Output.Reader.WaitToReadAsync(token).ConfigureAwait(false))
                    if (_Output.Reader.TryRead(out string? chunk)) return chunk;
                return null;
            }

            public Task WriteInputLineAsync(string line, CancellationToken token = default)
            {
                Inputs.Enqueue(line);
                return Task.CompletedTask;
            }

            public Task WaitForExitAsync(CancellationToken token = default) => _Exit.Task.WaitAsync(token);

            public void KillTree()
            {
                Killed = true;
                if (!ExitCode.HasValue) Exit(137);
            }

            public void Dispose() { }
        }

        private static async Task<bool> EventuallyAsync(Func<bool> condition)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                if (condition()) return true;
                await Task.Delay(20).ConfigureAwait(false);
            }
            return condition();
        }

        private static string Json(object value) => JsonSerializer.Serialize(value);

        private static UnixFileMode ModeOf(string path)
        {
            if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Unix file modes do not exist on this host.");
            return File.GetUnixFileMode(path);
        }

        private void AssertRefused(Action action, string code, string label)
        {
            try
            {
                action();
            }
            catch (AccountLoginException ex)
            {
                AssertEqual(code, ex.Code, label);
                return;
            }
            throw new Exception("Expected refusal " + code + ": " + label);
        }

        private async Task AssertRefusedAsync(Func<Task> action, string code, string label)
        {
            try
            {
                await action().ConfigureAwait(false);
            }
            catch (AccountLoginException ex)
            {
                AssertEqual(code, ex.Code, label);
                return;
            }
            throw new Exception("Expected refusal " + code + ": " + label);
        }

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {

            await RunTest("Account folder is derived on the server under the data directory and creation is idempotent", () =>
            {
                using (Harness h = new Harness())
                {
                    AccountLoginHomeResult first = h.Service.EnsureHome("codex-second");
                    AccountLoginHomeResult second = h.Service.EnsureHome("codex-second");
                    AssertEqual(Path.Combine(Path.GetFullPath(h.DataDirectory), "accounts", "codex-second"), first.HomeDirectory);
                    AssertTrue(first.Created, "the first call creates the folder");
                    AssertFalse(second.Created, "a repeated call is idempotent");
                    AssertEqual(Path.Combine(first.HomeDirectory, "cursor-api-key"), first.CursorKeyFile);
                    AssertTrue(Directory.Exists(first.HomeDirectory));
                }
            });

            if (!OperatingSystem.IsWindows())
            {
                await RunTest("Account folder and the accounts root are created with mode 0700", () =>
                {
                    using (Harness h = new Harness())
                    {
                        AccountLoginHomeResult home = h.Service.EnsureHome("private-home");
                        AssertEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, ModeOf(home.HomeDirectory));
                        AssertEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, ModeOf(AccountLoginPaths.AccountsRoot(h.DataDirectory)), "accounts root");
                    }
                });
            }
            else
            {
                SkipTest("account_folder_mode_requires_posix", "Unix file modes do not exist on this host.");
            }

            await RunTest("Unsafe or traversing account IDs are refused before any folder is created", async () =>
            {
                using (Harness h = new Harness())
                {
                    string[] unsafeIds = new[] { "..", "../escape", "a/b", "a\\b", ".hidden", "", " ", "a b", "-lead", "x.y", "%2e%2e", new string('a', 65), "/abs" };
                    foreach (string id in unsafeIds)
                    {
                        AssertRefused(() => h.Service.EnsureHome(id), AccountLoginService.ReasonAccountIdInvalid, "home for '" + id + "'");
                        AssertRefused(() => h.Service.SubmitKey(id, AgentRuntimeEnum.OpenCode, "key-value"), AccountLoginService.ReasonAccountIdInvalid, "key for '" + id + "'");
                        await AssertRefusedAsync(() => h.Service.StartAsync(id, AgentRuntimeEnum.Codex), AccountLoginService.ReasonAccountIdInvalid, "start for '" + id + "'").ConfigureAwait(false);
                    }
                    AssertEqual(0, h.Runner.Requests.Count, "no login process may start for an unsafe ID");
                    AssertEqual(0, Directory.GetFileSystemEntries(h.DataDirectory).Length, "nothing may be created anywhere under the data directory");
                    AssertFalse(AccountLoginPaths.IsSafeAccountId("../x"));
                    AssertTrue(AccountLoginPaths.IsSafeAccountId("opencode-go_2"));
                }
            });

            await RunTest("Codex device login returns only the verification URL and user code parsed from the output", async () =>
            {
                using (Harness h = new Harness())
                {
                    h.Runner.OnStart = process =>
                    {
                        process.Emit("\u001B[1mWelcome\u001B[0m\nNOISE-LINE-THAT-MUST-NOT-LEAK token=abc123\nSee https://phishing.example/device for help\n");
                        process.Emit("Open https://auth.openai.com/codex/device\nand enter the code: WXYZ-1234\nWaiting for approval...\n");
                    };
                    AccountLoginSession session = await h.Service.StartAsync("codex-second", AgentRuntimeEnum.Codex).ConfigureAwait(false);
                    AssertEqual(AccountLoginStateEnum.Pending, session.State);
                    AssertEqual(AccountLoginMethodEnum.DeviceCode, session.Method);
                    AssertEqual("https://auth.openai.com/codex/device", session.VerificationUrl);
                    AssertEqual("WXYZ-1234", session.UserCode);
                    string returned = Json(session);
                    AssertFalse(returned.Contains("NOISE-LINE") || returned.Contains("abc123") || returned.Contains("phishing") || returned.Contains("Welcome"), "no other output may be returned");
                    AssertFalse(h.Sinks().Contains("NOISE-LINE") || h.Sinks().Contains("WXYZ-1234") || h.Sinks().Contains("auth.openai.com"), "no output may be logged");

                    AccountLoginProcessRequest request = h.Runner.Requests.Single();
                    string home = h.Service.HomeFor("codex-second");
                    AssertEqual("codex", request.Executable);
                    AssertEqual("login --device-auth", String.Join(" ", request.Arguments));
                    AssertEqual(home, request.Environment["CODEX_HOME"]);
                    AssertEqual(home, request.WorkingDirectory);

                    h.Runner.Processes.Single().Exit(0);
                    AssertTrue(await EventuallyAsync(() => h.Service.GetSession("codex-second")!.State == AccountLoginStateEnum.Succeeded).ConfigureAwait(false), "a clean exit completes the login");
                    AssertTrue(await EventuallyAsync(() => h.Succeeded.Contains("codex-second")).ConfigureAwait(false), "success asks the server to refresh its login probe");
                    AssertTrue(h.Events.Any(e => e.StartsWith("account.login_succeeded")), "success emits an event");
                    AssertFalse(h.Sinks().Contains("NOISE-LINE"), "the event carries no output");
                }
            });

            await RunTest("A URL split across output chunks is returned whole, never truncated", async () =>
            {
                using (Harness h = new Harness())
                {
                    h.Runner.OnStart = process =>
                    {
                        process.Emit("Open https://auth.openai.com/co");
                        Task.Run(async () =>
                        {
                            await Task.Delay(100).ConfigureAwait(false);
                            process.Emit("dex/device then enter code ABCD-EFGH\n");
                        });
                    };
                    AccountLoginSession session = await h.Service.StartAsync("split", AgentRuntimeEnum.Codex).ConfigureAwait(false);
                    AssertEqual("https://auth.openai.com/codex/device", session.VerificationUrl);
                    AssertEqual("ABCD-EFGH", session.UserCode);
                }
            });

            await RunTest("Codex device login reads a code printed on the line after its label, in color, with uneven group lengths", async () =>
            {
                using (Harness h = new Harness())
                {
                    h.Runner.OnStart = process =>
                    {
                        process.Emit("\nWelcome to Codex [v[2m0.154.0[0m]\n\n"
                            + "1. Open this link in your browser and sign in to your account\n"
                            + "   [94mhttps://auth.openai.com/codex/device[0m\n\n"
                            + "2. Enter this one-time code [2m(expires in 15 minutes)[0m\n"
                            + "   [94mQ7RT-M2KP9[0m\n\n"
                            + "[2mContinue only if you started this login in Codex.[0m\n");
                    };
                    AccountLoginSession session = await h.Service.StartAsync("codex-colored", AgentRuntimeEnum.Codex).ConfigureAwait(false);
                    AssertEqual(AccountLoginStateEnum.Pending, session.State);
                    AssertEqual("https://auth.openai.com/codex/device", session.VerificationUrl);
                    AssertEqual("Q7RT-M2KP9", session.UserCode);
                }
            });

            await RunTest("A login process that exits with an error is Failed with a safe reason", async () =>
            {
                using (Harness h = new Harness())
                {
                    h.Runner.OnStart = process =>
                    {
                        process.Emit("error: something private happened\n");
                        process.Exit(1);
                    };
                    AccountLoginSession session = await h.Service.StartAsync("fails", AgentRuntimeEnum.Codex).ConfigureAwait(false);
                    AssertEqual(AccountLoginStateEnum.Failed, session.State);
                    AssertEqual(AccountLoginService.ReasonProcessFailed, session.Reason);
                    AssertFalse(Json(session).Contains("private"), "the error output is not returned");
                    AssertEqual(0, h.Succeeded.Count);
                }
            });

            await RunTest("Claude paste-code login relays the pasted code to the process stdin and never returns or logs it", async () =>
            {
                using (Harness h = new Harness())
                {
                    h.Runner.OnStart = process => process.Emit("Browser didn't open? Use the url below to sign in:\n\nhttps://claude.ai/oauth/authorize?code=true&client_id=example&state=s1\n\nPaste code here if prompted > ");
                    AccountLoginSession session = await h.Service.StartAsync("claude-second", AgentRuntimeEnum.ClaudeCode).ConfigureAwait(false);
                    AssertEqual(AccountLoginMethodEnum.PasteCode, session.Method);
                    AssertEqual("https://claude.ai/oauth/authorize?code=true&client_id=example&state=s1", session.VerificationUrl);
                    AssertTrue(session.NeedsCode, "the session waits for a pasted code");
                    AssertNull(session.UserCode);
                    AccountLoginProcessRequest request = h.Runner.Requests.Single();
                    AssertEqual("claude", request.Executable);
                    AssertEqual("auth login", String.Join(" ", request.Arguments));
                    AssertEqual(h.Service.HomeFor("claude-second"), request.Environment["CLAUDE_CONFIG_DIR"]);

                    const string pasted = "PASTED-OAUTH-CODE-value#state-s1";
                    AccountLoginSession afterCode = await h.Service.SubmitCodeAsync("claude-second", "  " + pasted + "\n").ConfigureAwait(false);
                    FakeProcess process = h.Runner.Processes.Single();
                    AssertEqual(pasted, process.Inputs.Single(), "exactly the trimmed code reaches stdin");
                    AssertFalse(afterCode.NeedsCode);
                    AssertFalse(Json(afterCode).Contains("PASTED-OAUTH"), "the code is not returned");
                    AssertFalse(h.Sinks().Contains("PASTED-OAUTH"), "the code is not logged");
                    process.Exit(0);
                    AssertTrue(await EventuallyAsync(() => h.Service.GetSession("claude-second")!.State == AccountLoginStateEnum.Succeeded).ConfigureAwait(false));
                    await AssertRefusedAsync(() => h.Service.SubmitCodeAsync("claude-second", "late"), AccountLoginService.ReasonNoPendingCode, "a finished login takes no code").ConfigureAwait(false);
                }
            });

            await RunTest("Cursor login runs with NO_OPEN_BROWSER and the account folder as HOME, leaving the shared login untouched", async () =>
            {
                using (Harness h = new Harness())
                {
                    h.Runner.OnStart = process => process.Emit("Please visit https://cursor.com/loginDeepControl?challenge=c&uuid=u&mode=login to log in\n");
                    AccountLoginSession session = await h.Service.StartAsync("cursor-second", AgentRuntimeEnum.Cursor).ConfigureAwait(false);
                    AssertEqual("https://cursor.com/loginDeepControl?challenge=c&uuid=u&mode=login", session.VerificationUrl);
                    AccountLoginProcessRequest request = h.Runner.Requests.Single();
                    string home = h.Service.HomeFor("cursor-second");
                    AssertEqual("cursor-agent", request.Executable);
                    AssertEqual("login", String.Join(" ", request.Arguments));
                    AssertEqual("1", request.Environment["NO_OPEN_BROWSER"]);
                    AssertEqual(home, request.Environment["HOME"]);
                }
            });

            await RunTest("OpenCode has no browser login; it is refused by name without starting a process", async () =>
            {
                using (Harness h = new Harness())
                {
                    await AssertRefusedAsync(() => h.Service.StartAsync("opencode-second", AgentRuntimeEnum.OpenCode), AccountLoginService.ReasonMethodUnsupported, "OpenCode start").ConfigureAwait(false);
                    AssertRefused(() => h.Service.SubmitKey("codex-second", AgentRuntimeEnum.Codex, "k"), AccountLoginService.ReasonMethodUnsupported, "Codex key");
                    AssertEqual(0, h.Runner.Requests.Count);
                }
            });

            await RunTest("A second start while a login is pending returns that login and starts no second process", async () =>
            {
                using (Harness h = new Harness())
                {
                    h.Runner.OnStart = process => process.Emit("Go to https://auth.openai.com/codex/device code QRST-7890\n");
                    AccountLoginSession first = await h.Service.StartAsync("one-login", AgentRuntimeEnum.Codex).ConfigureAwait(false);
                    AccountLoginSession second = await h.Service.StartAsync("one-login", AgentRuntimeEnum.Codex).ConfigureAwait(false);
                    AssertEqual(first.SessionId, second.SessionId);
                    AssertTrue(second.Reused);
                    AssertEqual(1, h.Runner.Requests.Count);
                    AssertRefused(() => h.Service.SubmitKey("one-login", AgentRuntimeEnum.Cursor, "k"), AccountLoginService.ReasonInProgress, "no key while a login is pending");
                }
            });

            await RunTest("A pending login past its lifetime is Expired and its process is killed", async () =>
            {
                using (Harness h = new Harness())
                {
                    h.Service.SessionLifetime = TimeSpan.FromMilliseconds(300);
                    h.Runner.OnStart = process => process.Emit("Open https://auth.openai.com/codex/device code LMNO-4567\n");
                    AccountLoginSession session = await h.Service.StartAsync("expires", AgentRuntimeEnum.Codex).ConfigureAwait(false);
                    AssertEqual(AccountLoginStateEnum.Pending, session.State);
                    FakeProcess process = h.Runner.Processes.Single();
                    AssertTrue(await EventuallyAsync(() => process.Killed).ConfigureAwait(false), "the expired login process must be killed");
                    AccountLoginSession expired = h.Service.GetSession("expires")!;
                    AssertEqual(AccountLoginStateEnum.Expired, expired.State);
                    AssertEqual(AccountLoginService.ReasonExpired, expired.Reason);
                    AssertNull(expired.VerificationUrl, "an expired login no longer offers its URL");
                    AssertEqual(0, h.Succeeded.Count, "the kill is not a success");
                }
            });

            await RunTest("Cancel stops the pending login process", async () =>
            {
                using (Harness h = new Harness())
                {
                    h.Runner.OnStart = process => process.Emit("Open https://auth.openai.com/codex/device code HJKL-2468\n");
                    await h.Service.StartAsync("cancelled", AgentRuntimeEnum.Codex).ConfigureAwait(false);
                    AccountLoginSession? cancelled = h.Service.Cancel("cancelled");
                    AssertEqual(AccountLoginStateEnum.Cancelled, cancelled!.State);
                    AssertTrue(h.Runner.Processes.Single().Killed);
                    await Task.Delay(100).ConfigureAwait(false);
                    AssertEqual(AccountLoginStateEnum.Cancelled, h.Service.GetSession("cancelled")!.State, "the killed exit does not overwrite the cancellation");
                }
            });

            await RunTest("A CLI that prints no verification URL is stopped with a named reason", async () =>
            {
                using (Harness h = new Harness())
                {
                    h.Service.PromptTimeout = TimeSpan.FromMilliseconds(200);
                    h.Runner.OnStart = process => process.Emit("Unexpected interactive menu\n> ");
                    AccountLoginSession session = await h.Service.StartAsync("no-prompt", AgentRuntimeEnum.Codex).ConfigureAwait(false);
                    AssertEqual(AccountLoginStateEnum.Failed, session.State);
                    AssertEqual(AccountLoginService.ReasonPromptNotFound, session.Reason);
                    AssertTrue(h.Runner.Processes.Single().Killed);
                }
            });

            await RunTest("A CLI that cannot start is Failed with account_login_cli_unavailable", async () =>
            {
                using (Harness h = new Harness())
                {
                    h.Runner.Failure = new Win32Exception(2, "No such file or directory");
                    AccountLoginSession session = await h.Service.StartAsync("no-cli", AgentRuntimeEnum.ClaudeCode).ConfigureAwait(false);
                    AssertEqual(AccountLoginStateEnum.Failed, session.State);
                    AssertEqual(AccountLoginService.ReasonCliUnavailable, session.Reason);
                }
            });

            await RunTest("OpenCode key login merges the opencode-go API entry, keeps other entries, and never echoes or logs the key", () =>
            {
                using (Harness h = new Harness())
                {
                    const string key = "sk-opencode-SECRET-VALUE-0123456789";
                    string home = h.Service.EnsureHome("opencode-second").HomeDirectory;
                    Directory.CreateDirectory(Path.Combine(home, "opencode"));
                    string file = Path.Combine(home, "opencode", "auth.json");
                    File.WriteAllText(file, "{\"other-provider\":{\"type\":\"api\",\"key\":\"keep-me\"}}");
                    AccountLoginSession session = h.Service.SubmitKey("opencode-second", AgentRuntimeEnum.OpenCode, key);
                    AssertEqual(AccountLoginStateEnum.Succeeded, session.State);
                    AssertEqual(AccountLoginMethodEnum.ApiKey, session.Method);
                    JsonObject stored = (JsonObject)JsonNode.Parse(File.ReadAllText(file))!;
                    AssertEqual("keep-me", (string?)stored["other-provider"]!["key"], "other providers survive the merge");
                    AssertEqual("api", (string?)stored["opencode-go"]!["type"]);
                    AssertEqual(key, (string?)stored["opencode-go"]!["key"]);
                    AssertFalse(Json(session).Contains("SECRET-VALUE"), "the key is not returned");
                    AssertFalse(Json(h.Service.GetSession("opencode-second")!).Contains("SECRET-VALUE"), "status does not return the key");
                    AssertFalse(h.Sinks().Contains("SECRET-VALUE"), "the key is not logged or sent in events");
                    AssertTrue(h.Succeeded.Contains("opencode-second"));
                    AssertEqual(0, Directory.GetFiles(Path.Combine(home, "opencode"), "*.tmp-*").Length, "no temporary copy of the key is left behind");
                }
            });

            await RunTest("OpenCode key login refuses to replace an unreadable credential file", () =>
            {
                using (Harness h = new Harness())
                {
                    string home = h.Service.EnsureHome("opencode-broken").HomeDirectory;
                    Directory.CreateDirectory(Path.Combine(home, "opencode"));
                    string file = Path.Combine(home, "opencode", "auth.json");
                    File.WriteAllText(file, "not json");
                    AssertRefused(() => h.Service.SubmitKey("opencode-broken", AgentRuntimeEnum.OpenCode, "sk-value"), AccountLoginService.ReasonAuthFileInvalid, "invalid file");
                    AssertEqual("not json", File.ReadAllText(file), "the existing file is left unchanged");
                    AssertRefused(() => h.Service.SubmitKey("opencode-broken", AgentRuntimeEnum.OpenCode, "has space"), AccountLoginService.ReasonInputInvalid, "malformed key");
                    AssertRefused(() => h.Service.SubmitKey("opencode-broken", AgentRuntimeEnum.OpenCode, "   "), AccountLoginService.ReasonInputInvalid, "empty key");
                }
            });

            if (!OperatingSystem.IsWindows())
            {
                await RunTest("Stored key files are mode 0600", () =>
                {
                    using (Harness h = new Harness())
                    {
                        UnixFileMode ownerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                        h.Service.SubmitKey("opencode-mode", AgentRuntimeEnum.OpenCode, "sk-mode-test");
                        AssertEqual(ownerOnly, ModeOf(Path.Combine(h.Service.HomeFor("opencode-mode"), "opencode", "auth.json")), "OpenCode auth.json");
                        AssertEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                            ModeOf(Path.Combine(h.Service.HomeFor("opencode-mode"), "opencode")), "OpenCode data folder");
                        h.Service.SubmitKey("cursor-mode", AgentRuntimeEnum.Cursor, "cursor-mode-test");
                        AssertEqual(ownerOnly, ModeOf(Path.Combine(h.Service.HomeFor("cursor-mode"), AccountLoginPaths.CursorKeyFileName)), "Cursor key file");
                    }
                });
            }
            else
            {
                SkipTest("stored_key_file_mode_requires_posix", "Unix file modes do not exist on this host.");
            }

            await RunTest("Cursor key login stores a key file that a captain on the account launches with as CURSOR_API_KEY", () =>
            {
                using (Harness h = new Harness())
                {
                    const string key = "cursor-SECRET-KEY-value";
                    AccountLoginSession session = h.Service.SubmitKey("cursor-key", AgentRuntimeEnum.Cursor, key);
                    AssertFalse(Json(session).Contains("SECRET-KEY") || h.Sinks().Contains("SECRET-KEY"), "the key is not returned or logged");
                    string keyFile = AccountLoginPaths.CursorKeyFileFor(h.DataDirectory, "cursor-key");
                    UsageAccountSettings account = new UsageAccountSettings { Id = "cursor-key", Runtime = AgentRuntimeEnum.Cursor, LaunchCredentialFile = keyFile };
                    CaptainAccountLaunch.ValidateAccount(account, AccountLoginPaths.AccountsRoot(h.DataDirectory));
                    Dictionary<string, string> environment = CaptainAccountLaunch.BuildEnvironment(AgentRuntimeEnum.Cursor, account);
                    AssertEqual(key, environment["CURSOR_API_KEY"]);
                    AssertFalse(JsonSerializer.Serialize(account).Contains("SECRET-KEY"), "settings hold only the file path");

                    File.Delete(keyFile);
                    try
                    {
                        CaptainAccountLaunch.BuildEnvironment(AgentRuntimeEnum.Cursor, account);
                        throw new Exception("A missing key file must refuse the launch");
                    }
                    catch (CaptainAccountLaunchException ex)
                    {
                        AssertEqual(CaptainAccountLaunch.ReasonCredentialUnavailable, ex.Code);
                    }
                    AssertEqual(CaptainAccountLaunch.ReasonCredentialUnavailable, CaptainAccountLaunch.CheckReadiness(account));
                }
            });

            await RunTest("launchCredentialFile must be the key file inside the account's own folder", () =>
            {
                string root = Path.Combine(Path.GetTempPath(), "armada_login_root", "accounts");
                string good = Path.Combine(root, "cursor-a", "cursor-api-key");
                CaptainAccountLaunch.ValidateAccount(new UsageAccountSettings { Id = "cursor-a", Runtime = AgentRuntimeEnum.Cursor, LaunchCredentialFile = good }, root);
                string[] rejected = new[]
                {
                    Path.Combine(root, "cursor-b", "cursor-api-key"),
                    Path.Combine(root, "cursor-a", "other-file"),
                    Path.Combine(root, "cursor-a", "..", "cursor-a", "cursor-api-key"),
                    Path.Combine(Path.GetTempPath(), "elsewhere", "cursor-a", "cursor-api-key"),
                    "cursor-a/cursor-api-key"
                };
                foreach (string path in rejected)
                    AssertThrows<ArgumentException>(() => CaptainAccountLaunch.ValidateAccount(new UsageAccountSettings { Id = "cursor-a", Runtime = AgentRuntimeEnum.Cursor, LaunchCredentialFile = path }, root), path);
                AssertThrows<ArgumentException>(() => CaptainAccountLaunch.ValidateAccount(new UsageAccountSettings { Id = "cursor-a", Runtime = AgentRuntimeEnum.Cursor, LaunchCredentialFile = good, LaunchCredentialEnv = "CURSOR_KEY_VAR" }, root), "env and file together");
                AssertThrows<ArgumentException>(() => CaptainAccountLaunch.ValidateAccount(new UsageAccountSettings { Id = "cursor-a", Runtime = AgentRuntimeEnum.Codex, LaunchCredentialFile = good }, root), "file on a non-Cursor account");
                AssertThrows<ArgumentException>(() => CaptainAccountLaunch.ValidateAccount(new UsageAccountSettings { Id = "cursor-a", LaunchCredentialFile = good }, root), "file without a runtime");
            });

            await RunTest("Account login routes require settings write permission: only a global administrator is admitted", () =>
            {
                AuthorizationService authz = new AuthorizationService();
                AssertTrue(UsageAccountLoginRoutes.IsPermitted(new AuthContext { IsAuthenticated = true, IsAdmin = true }, authz), "global administrator");
                AssertFalse(UsageAccountLoginRoutes.IsPermitted(new AuthContext { IsAuthenticated = true, IsTenantAdmin = true, TenantId = "ten_example" }, authz), "tenant administrator");
                AssertFalse(UsageAccountLoginRoutes.IsPermitted(new AuthContext { IsAuthenticated = true, TenantId = "ten_example", UserId = "usr_example" }, authz), "ordinary user");
                AssertFalse(UsageAccountLoginRoutes.IsPermitted(new AuthContext(), authz), "unauthenticated caller");
            });

            await RunTest("Request history never records account login requests, which carry keys and codes", () =>
            {
                ArmadaSettings settings = new ArmadaSettings { RequestHistoryEnabled = true };
                RequestHistoryCaptureService capture = new RequestHistoryCaptureService(settings);
                AssertFalse(capture.ShouldCapture("/api/v1/usage-accounts/cursor-a/login/key"));
                AssertFalse(capture.ShouldCapture("/api/v1/usage-accounts/claude-a/login/code"));
                AssertTrue(capture.ShouldCapture("/api/v1/fleets"), "other routes keep their capture");
            });
        }
    }
}
