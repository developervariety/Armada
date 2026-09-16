namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.ComponentModel;
    using System.IO;
    using System.Linq;
    using System.Text;
    using System.Text.Json;
    using System.Text.Json.Nodes;
    using System.Text.RegularExpressions;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;

    /// <summary>
    /// Drives subscription account logins from the dashboard. Each account gets a server-derived folder under the data
    /// directory. Device and paste-code logins run the runtime's own login command inside that folder with the
    /// runtime's environment switch; only the verification URL and user code are parsed from its output, and the rest
    /// is discarded. API-key logins write the key once into the account's own credential file. Keys, pasted codes, and
    /// process output are never logged, returned, or stored in settings, events, or the database. One login runs per
    /// account, and a pending login is stopped when its lifetime ends.
    /// </summary>
    public sealed class AccountLoginService : IDisposable
    {
        #region Public-Members

        /// <summary>The account ID is not a safe folder name.</summary>
        public const string ReasonAccountIdInvalid = "account_id_invalid";

        /// <summary>The runtime does not support the requested login method.</summary>
        public const string ReasonMethodUnsupported = "account_login_method_unsupported";

        /// <summary>A login for this account is already pending.</summary>
        public const string ReasonInProgress = "account_login_in_progress";

        /// <summary>No pending login waits for a pasted code.</summary>
        public const string ReasonNoPendingCode = "account_login_not_waiting_for_code";

        /// <summary>The submitted key or code is empty or malformed.</summary>
        public const string ReasonInputInvalid = "account_login_input_invalid";

        /// <summary>The runtime CLI could not be started.</summary>
        public const string ReasonCliUnavailable = "account_login_cli_unavailable";

        /// <summary>The CLI did not print a verification URL (and code, where required) in time.</summary>
        public const string ReasonPromptNotFound = "account_login_prompt_not_found";

        /// <summary>The CLI exited without a completed login.</summary>
        public const string ReasonProcessFailed = "account_login_process_failed";

        /// <summary>The login was not finished within its lifetime.</summary>
        public const string ReasonExpired = "account_login_expired_before_completion";

        /// <summary>The operator cancelled the login.</summary>
        public const string ReasonCancelled = "account_login_cancelled";

        /// <summary>The existing credential file is not valid JSON, so it is left untouched.</summary>
        public const string ReasonAuthFileInvalid = "account_auth_file_invalid";

        /// <summary>The credential could not be written.</summary>
        public const string ReasonWriteFailed = "account_credential_write_failed";

        /// <summary>OpenCode provider entry the subscription key is stored under.</summary>
        public const string OpenCodeProvider = "opencode-go";

        /// <summary>Bound on a pending login, after which its process is stopped. At most 1 hour; default 15 minutes.</summary>
        public TimeSpan SessionLifetime
        {
            get => _SessionLifetime;
            set => _SessionLifetime = value < TimeSpan.FromMilliseconds(50) ? TimeSpan.FromMilliseconds(50) : value > TimeSpan.FromHours(1) ? TimeSpan.FromHours(1) : value;
        }

        /// <summary>How long a start waits for the CLI to print its prompt. Default 30 seconds.</summary>
        public TimeSpan PromptTimeout
        {
            get => _PromptTimeout;
            set => _PromptTimeout = value < TimeSpan.FromMilliseconds(50) ? TimeSpan.FromMilliseconds(50) : value > TimeSpan.FromMinutes(5) ? TimeSpan.FromMinutes(5) : value;
        }

        /// <summary>Resolves the CLI a login runs for a runtime. Replaceable for tests and custom installs.</summary>
        public Func<AgentRuntimeEnum, string> Executable { get; set; } = DefaultExecutable;

        /// <summary>Invoked with the account ID after a login succeeds, so the server can refresh its login check.</summary>
        public Action<string>? OnLoginSucceeded { get; set; }

        #endregion

        #region Private-Members

        private const int _MaxPromptChars = 65536;
        private const int _MaxKeyChars = 1024;
        private const int _MaxCodeChars = 4096;
        private const int _MaxAuthFileBytes = 65536;

        private static readonly Regex _Ansi = new Regex("\u001B\\[[0-9;?]*[ -/]*[@-~]|\u001B\\][^\u0007\u001B]*(\u0007|\u001B\\\\)", RegexOptions.CultureInvariant);
        private static readonly Regex _Url = new Regex("https://[^\\s\"'<>`]+", RegexOptions.CultureInvariant);
        // A device code is upper-case letters and digits in dash-separated groups. CLIs print it after its label on the
        // same line or on the next line, and the label line can carry other text such as an expiry note.
        private static readonly Regex _LabelledCode = new Regex("\\bcode\\b[^\\r\\n]*?(?:\\r?\\n[ \\t]*)?(?<![A-Za-z0-9-])(?-i:([A-Z0-9]{3,10}(?:-[A-Z0-9]{3,10})+))(?![A-Za-z0-9-])", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        private static readonly Regex _BareCode = new Regex("(?<![A-Za-z0-9/._=-])([A-Z0-9]{4,8}-[A-Z0-9]{4,8})(?![A-Za-z0-9-])", RegexOptions.CultureInvariant);

        private readonly string _DataDirectory;
        private readonly IAccountLoginProcessRunner _Runner;
        private readonly Action<string> _Log;
        private readonly Action<string, string, string>? _EmitEvent;
        private readonly object _Lock = new object();
        private readonly Dictionary<string, LoginEntry> _Sessions = new Dictionary<string, LoginEntry>(StringComparer.OrdinalIgnoreCase);
        private TimeSpan _SessionLifetime = TimeSpan.FromMinutes(15);
        private TimeSpan _PromptTimeout = TimeSpan.FromSeconds(30);
        private bool _Disposed;

        #endregion

        #region Constructors-and-Factories

        /// <summary>Instantiate.</summary>
        /// <param name="dataDirectory">Admiral data directory; account folders are created under its accounts folder.</param>
        /// <param name="runner">Process runner.</param>
        /// <param name="log">Log sink. Receives account IDs, runtimes, states, and reason codes only.</param>
        /// <param name="emitEvent">Optional event sink: event type, message, account ID. Same content rule as the log.</param>
        public AccountLoginService(string dataDirectory, IAccountLoginProcessRunner runner, Action<string>? log = null, Action<string, string, string>? emitEvent = null)
        {
            if (String.IsNullOrWhiteSpace(dataDirectory)) throw new ArgumentNullException(nameof(dataDirectory));
            _DataDirectory = dataDirectory;
            _Runner = runner ?? throw new ArgumentNullException(nameof(runner));
            _Log = log ?? (_ => { });
            _EmitEvent = emitEvent;
        }

        #endregion

        #region Public-Methods

        /// <summary>Default CLI for a runtime's login command.</summary>
        public static string DefaultExecutable(AgentRuntimeEnum runtime)
        {
            return runtime switch
            {
                AgentRuntimeEnum.ClaudeCode => "claude",
                AgentRuntimeEnum.Codex => "codex",
                AgentRuntimeEnum.Cursor => "cursor-agent",
                AgentRuntimeEnum.OpenCode => "opencode",
                _ => throw new ArgumentException("Runtime " + runtime + " has no account login.", nameof(runtime))
            };
        }

        /// <summary>True when the runtime logs in by running its CLI (device or paste-code flow).</summary>
        public static bool SupportsInteractiveLogin(AgentRuntimeEnum runtime)
        {
            return runtime == AgentRuntimeEnum.Codex || runtime == AgentRuntimeEnum.ClaudeCode || runtime == AgentRuntimeEnum.Cursor;
        }

        /// <summary>True when the runtime accepts an API key login.</summary>
        public static bool SupportsKeyLogin(AgentRuntimeEnum runtime)
        {
            return runtime == AgentRuntimeEnum.OpenCode || runtime == AgentRuntimeEnum.Cursor;
        }

        /// <summary>The server-derived folder for an account. Throws <see cref="AccountLoginException"/> for an unsafe ID.</summary>
        public string HomeFor(string accountId)
        {
            RequireSafeId(accountId);
            return AccountLoginPaths.HomeFor(_DataDirectory, accountId);
        }

        /// <summary>Create the account folder with owner-only permissions. Idempotent.</summary>
        public AccountLoginHomeResult EnsureHome(string accountId)
        {
            string home = HomeFor(accountId);
            bool created = !Directory.Exists(home);
            try
            {
                CreatePrivateDirectory(AccountLoginPaths.AccountsRoot(_DataDirectory));
                CreatePrivateDirectory(home);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                throw new AccountLoginException(ReasonWriteFailed, 500, "The account folder could not be created.");
            }
            if (created) _Log("account " + accountId + " folder created");
            return new AccountLoginHomeResult
            {
                AccountId = accountId,
                HomeDirectory = home,
                CursorKeyFile = Path.Combine(home, AccountLoginPaths.CursorKeyFileName),
                Created = created
            };
        }

        /// <summary>
        /// Start the runtime's login command in the account folder and return once it printed its verification URL (and
        /// user code, for device logins), failed, or timed out. A pending login for the account is returned as is.
        /// </summary>
        public async Task<AccountLoginSession> StartAsync(string accountId, AgentRuntimeEnum runtime, CancellationToken token = default)
        {
            RequireSafeId(accountId);
            if (!SupportsInteractiveLogin(runtime))
                throw new AccountLoginException(ReasonMethodUnsupported, 400, "Runtime " + runtime + " logs in with an API key, not a browser login.");
            AccountLoginHomeResult home = EnsureHome(accountId);
            LoginEntry entry;
            lock (_Lock)
            {
                ThrowIfDisposed();
                if (_Sessions.TryGetValue(accountId, out LoginEntry? existing) && existing.View.State == AccountLoginStateEnum.Pending)
                {
                    AccountLoginSession reused = Snapshot(existing);
                    reused.Reused = true;
                    return reused;
                }
                DateTime now = DateTime.UtcNow;
                entry = new LoginEntry
                {
                    View = new AccountLoginSession
                    {
                        SessionId = "alg_" + Guid.NewGuid().ToString("N"),
                        AccountId = accountId,
                        Runtime = runtime,
                        Method = runtime == AgentRuntimeEnum.ClaudeCode ? AccountLoginMethodEnum.PasteCode : AccountLoginMethodEnum.DeviceCode,
                        State = AccountLoginStateEnum.Pending,
                        StartedUtc = now,
                        ExpiresUtc = now.Add(_SessionLifetime)
                    }
                };
                _Sessions[accountId] = entry;
            }

            AccountLoginProcessRequest request = BuildRequest(runtime, home.HomeDirectory);
            request.Executable = Executable(runtime);
            try
            {
                entry.Process = _Runner.Start(request);
            }
            catch (Exception ex) when (ex is Win32Exception || ex is FileNotFoundException || ex is InvalidOperationException || ex is IOException || ex is UnauthorizedAccessException)
            {
                Finish(entry, AccountLoginStateEnum.Failed, ReasonCliUnavailable);
                return Snapshot(entry);
            }

            _Log("account " + accountId + " " + runtime + " login started");
            entry.Lifetime.CancelAfter(_SessionLifetime);
            Task pump = PumpOutputAsync(entry);
            entry.Completion = WatchExitAsync(entry, pump);

            Task delay = Task.Delay(_PromptTimeout, token);
            await Task.WhenAny(entry.PromptFound.Task, entry.Completion, delay).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            bool prompted = entry.PromptFound.Task.IsCompleted;
            if (!prompted && !entry.Completion.IsCompleted)
            {
                // Without a URL the owner cannot finish the login, so a waiting process is only a leak.
                Finish(entry, AccountLoginStateEnum.Failed, ReasonPromptNotFound);
                entry.Process.KillTree();
            }
            else if (!prompted)
            {
                // The process ended first; let the exit watcher record the outcome before it is returned.
                await entry.Completion.ConfigureAwait(false);
            }
            return Snapshot(entry);
        }

        /// <summary>Relay the code the owner pasted back to a pending paste-code login.</summary>
        public async Task<AccountLoginSession> SubmitCodeAsync(string accountId, string? code, CancellationToken token = default)
        {
            RequireSafeId(accountId);
            string value = RequireSingleLine(code, _MaxCodeChars, false);
            LoginEntry? entry;
            lock (_Lock) _Sessions.TryGetValue(accountId, out entry);
            if (entry == null || entry.Process == null || entry.View.State != AccountLoginStateEnum.Pending || entry.View.Method != AccountLoginMethodEnum.PasteCode)
                throw new AccountLoginException(ReasonNoPendingCode, 409, "No pending login for this account is waiting for a code.");
            try
            {
                await entry.Process.WriteInputLineAsync(value, token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException || ex is InvalidOperationException)
            {
                throw new AccountLoginException(ReasonProcessFailed, 409, "The login process is no longer accepting input.");
            }
            lock (_Lock) entry.View.NeedsCode = false;
            _Log("account " + accountId + " login code relayed");
            return Snapshot(entry);
        }

        /// <summary>
        /// Store an API key once. OpenCode: merged as the <see cref="OpenCodeProvider"/> API entry of
        /// <c>&lt;folder&gt;/opencode/auth.json</c>. Cursor: written to the account's key file. Files are owner-only.
        /// </summary>
        public AccountLoginSession SubmitKey(string accountId, AgentRuntimeEnum runtime, string? apiKey)
        {
            RequireSafeId(accountId);
            if (!SupportsKeyLogin(runtime))
                throw new AccountLoginException(ReasonMethodUnsupported, 400, "Runtime " + runtime + " does not log in with an API key.");
            string key = RequireSingleLine(apiKey, _MaxKeyChars, true);
            AccountLoginHomeResult home = EnsureHome(accountId);
            lock (_Lock)
            {
                ThrowIfDisposed();
                if (_Sessions.TryGetValue(accountId, out LoginEntry? existing) && existing.View.State == AccountLoginStateEnum.Pending)
                    throw new AccountLoginException(ReasonInProgress, 409, "A login for this account is pending; cancel it first.");
            }

            if (runtime == AgentRuntimeEnum.OpenCode)
            {
                string folder = Path.Combine(home.HomeDirectory, "opencode");
                string file = Path.Combine(folder, "auth.json");
                JsonObject root;
                try
                {
                    CreatePrivateDirectory(folder);
                    root = ReadAuthFile(file);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    throw new AccountLoginException(ReasonWriteFailed, 500, "The OpenCode credential file could not be read.");
                }
                JsonObject entryNode = new JsonObject { ["type"] = "api", ["key"] = key };
                root[OpenCodeProvider] = entryNode;
                WritePrivateFile(file, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            }
            else
            {
                WritePrivateFile(home.CursorKeyFile, key + "\n");
            }

            DateTime now = DateTime.UtcNow;
            LoginEntry record = new LoginEntry
            {
                View = new AccountLoginSession
                {
                    SessionId = "alg_" + Guid.NewGuid().ToString("N"),
                    AccountId = accountId,
                    Runtime = runtime,
                    Method = AccountLoginMethodEnum.ApiKey,
                    State = AccountLoginStateEnum.Succeeded,
                    StartedUtc = now,
                    CompletedUtc = now
                }
            };
            lock (_Lock) _Sessions[accountId] = record;
            _Log("account " + accountId + " " + runtime + " key stored");
            _EmitEvent?.Invoke("account.login_succeeded", "Account " + accountId + " " + runtime + " key login stored", accountId);
            OnLoginSucceeded?.Invoke(accountId);
            return Snapshot(record);
        }

        /// <summary>The last login for the account, or null.</summary>
        public AccountLoginSession? GetSession(string accountId)
        {
            RequireSafeId(accountId);
            lock (_Lock) return _Sessions.TryGetValue(accountId, out LoginEntry? entry) ? Snapshot(entry) : null;
        }

        /// <summary>Cancel a pending login and stop its process. Returns the last login, or null when there is none.</summary>
        public AccountLoginSession? Cancel(string accountId)
        {
            RequireSafeId(accountId);
            LoginEntry? entry;
            lock (_Lock) _Sessions.TryGetValue(accountId, out entry);
            if (entry == null) return null;
            if (Finish(entry, AccountLoginStateEnum.Cancelled, ReasonCancelled)) entry.Process?.KillTree();
            return Snapshot(entry);
        }

        /// <summary>Stop every pending login.</summary>
        public void Dispose()
        {
            List<LoginEntry> entries;
            lock (_Lock)
            {
                if (_Disposed) return;
                _Disposed = true;
                entries = _Sessions.Values.ToList();
            }
            foreach (LoginEntry entry in entries)
            {
                if (Finish(entry, AccountLoginStateEnum.Cancelled, ReasonCancelled)) entry.Process?.KillTree();
            }
        }

        #endregion

        #region Private-Methods

        private static void RequireSafeId(string accountId)
        {
            if (!AccountLoginPaths.IsSafeAccountId(accountId))
                throw new AccountLoginException(ReasonAccountIdInvalid, 400, "Account ID must be 1-64 letters, digits, hyphens, or underscores, starting with a letter or digit.");
        }

        private static string RequireSingleLine(string? value, int maxChars, bool forbidWhitespace)
        {
            string trimmed = (value ?? String.Empty).Trim();
            if (trimmed.Length == 0 || trimmed.Length > maxChars || trimmed.Any(Char.IsControl) || (forbidWhitespace && trimmed.Any(Char.IsWhiteSpace)))
                throw new AccountLoginException(ReasonInputInvalid, 400, "The submitted value is empty or malformed.");
            return trimmed;
        }

        private void ThrowIfDisposed()
        {
            if (_Disposed) throw new AccountLoginException(ReasonCancelled, 503, "Account logins are stopping.");
        }

        private static AccountLoginProcessRequest BuildRequest(AgentRuntimeEnum runtime, string home)
        {
            AccountLoginProcessRequest request = new AccountLoginProcessRequest { WorkingDirectory = home };
            switch (runtime)
            {
                case AgentRuntimeEnum.Codex:
                    request.Arguments.Add("login");
                    request.Arguments.Add("--device-auth");
                    request.Environment[CaptainAccountLaunch.CodexHomeVariable] = home;
                    break;
                case AgentRuntimeEnum.ClaudeCode:
                    request.Arguments.Add("auth");
                    request.Arguments.Add("login");
                    request.Environment[CaptainAccountLaunch.ClaudeConfigDirVariable] = home;
                    break;
                case AgentRuntimeEnum.Cursor:
                    // Cursor stores its browser login under HOME. Pointing HOME at the account folder keeps the
                    // Admiral user's own Cursor login untouched.
                    request.Arguments.Add("login");
                    request.Environment["NO_OPEN_BROWSER"] = "1";
                    request.Environment["HOME"] = home;
                    request.Environment["XDG_CONFIG_HOME"] = Path.Combine(home, ".config");
                    break;
                default:
                    throw new AccountLoginException(ReasonMethodUnsupported, 400, "Runtime " + runtime + " has no browser login.");
            }
            return request;
        }

        private async Task PumpOutputAsync(LoginEntry entry)
        {
            IAccountLoginProcess process = entry.Process!;
            StringBuilder text = new StringBuilder();
            bool found = false;
            try
            {
                string? chunk;
                while ((chunk = await process.ReadOutputAsync(CancellationToken.None).ConfigureAwait(false)) != null)
                {
                    // Keep draining after the prompt so the child never blocks on a full pipe; the rest is discarded.
                    if (found || text.Length >= _MaxPromptChars) continue;
                    text.Append(chunk, 0, Math.Min(chunk.Length, _MaxPromptChars - text.Length));
                    found = TryApplyPrompt(entry, text.ToString(), false);
                }
                if (!found) TryApplyPrompt(entry, text.ToString(), true);
            }
            catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException || ex is InvalidOperationException)
            {
                // Output ended abnormally; the exit watcher records the outcome.
            }
        }

        private bool TryApplyPrompt(LoginEntry entry, string raw, bool outputEnded)
        {
            AgentRuntimeEnum runtime = entry.View.Runtime;
            string text = _Ansi.Replace(raw, String.Empty);
            string? url = FindUrl(runtime, text, outputEnded);
            string? code = runtime == AgentRuntimeEnum.Codex ? FindCode(text, outputEnded) : null;
            bool complete = url != null && (runtime != AgentRuntimeEnum.Codex || code != null);
            if (!complete) return false;
            lock (_Lock)
            {
                entry.View.VerificationUrl = url;
                entry.View.UserCode = code;
                entry.View.NeedsCode = entry.View.Method == AccountLoginMethodEnum.PasteCode && entry.View.State == AccountLoginStateEnum.Pending;
            }
            entry.PromptFound.TrySetResult(true);
            return true;
        }

        /// <summary>
        /// The first https URL on the runtime provider's own domain. A URL that runs to the end of the text so far may
        /// still be arriving, so it counts only once output has ended.
        /// </summary>
        internal static string? FindUrl(AgentRuntimeEnum runtime, string text, bool outputEnded)
        {
            foreach (Match match in _Url.Matches(text))
            {
                if (!outputEnded && match.Index + match.Length >= text.Length) continue;
                string candidate = match.Value.TrimEnd('.', ',', ';', ':', ')', ']', '}', '>');
                if (!Uri.TryCreate(candidate, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps) continue;
                if (IsProviderHost(runtime, uri.Host)) return candidate;
            }
            return null;
        }

        /// <summary>A device user code such as ABCD-EFGH, preferring one labelled as a code.</summary>
        internal static string? FindCode(string text, bool outputEnded)
        {
            foreach (Regex pattern in new[] { _LabelledCode, _BareCode })
            {
                foreach (Match match in pattern.Matches(text))
                {
                    Group group = match.Groups[1];
                    if (!outputEnded && group.Index + group.Length >= text.Length) continue;
                    return group.Value;
                }
            }
            return null;
        }

        private static bool IsProviderHost(AgentRuntimeEnum runtime, string host)
        {
            string[] domains = runtime switch
            {
                AgentRuntimeEnum.Codex => new[] { "openai.com", "chatgpt.com" },
                AgentRuntimeEnum.ClaudeCode => new[] { "claude.ai", "claude.com", "anthropic.com" },
                AgentRuntimeEnum.Cursor => new[] { "cursor.com", "cursor.sh" },
                _ => Array.Empty<string>()
            };
            return domains.Any(d => String.Equals(host, d, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + d, StringComparison.OrdinalIgnoreCase));
        }

        private async Task WatchExitAsync(LoginEntry entry, Task pump)
        {
            IAccountLoginProcess process = entry.Process!;
            try
            {
                await process.WaitForExitAsync(entry.Lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (Finish(entry, AccountLoginStateEnum.Expired, ReasonExpired))
                    _Log("account " + entry.View.AccountId + " login expired");
                process.KillTree();
                entry.PromptFound.TrySetResult(false);
                return;
            }
            // Let the output pump see the final text before the outcome is decided.
            try { await pump.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch (TimeoutException) { /* A detached grandchild holds the pipe; the exit code still decides. */ }
            int? exitCode = process.ExitCode;
            if (exitCode == 0)
            {
                if (Finish(entry, AccountLoginStateEnum.Succeeded, null))
                {
                    _EmitEvent?.Invoke("account.login_succeeded", "Account " + entry.View.AccountId + " " + entry.View.Runtime + " login succeeded", entry.View.AccountId);
                    OnLoginSucceeded?.Invoke(entry.View.AccountId);
                }
            }
            else if (Finish(entry, AccountLoginStateEnum.Failed, ReasonProcessFailed))
            {
                _EmitEvent?.Invoke("account.login_failed", "Account " + entry.View.AccountId + " " + entry.View.Runtime + " login failed: " + ReasonProcessFailed, entry.View.AccountId);
            }
            entry.PromptFound.TrySetResult(false);
            entry.Lifetime.Dispose();
        }

        /// <summary>Move a pending login to a final state. Returns false when it was already final.</summary>
        private bool Finish(LoginEntry entry, AccountLoginStateEnum state, string? reason)
        {
            lock (_Lock)
            {
                if (entry.View.State != AccountLoginStateEnum.Pending) return false;
                entry.View.State = state;
                entry.View.Reason = reason;
                entry.View.NeedsCode = false;
                entry.View.CompletedUtc = DateTime.UtcNow;
                if (state != AccountLoginStateEnum.Succeeded)
                {
                    entry.View.VerificationUrl = null;
                    entry.View.UserCode = null;
                }
            }
            _Log("account " + entry.View.AccountId + " login " + state + (reason == null ? String.Empty : ": " + reason));
            return true;
        }

        private AccountLoginSession Snapshot(LoginEntry entry)
        {
            lock (_Lock)
            {
                AccountLoginSession v = entry.View;
                return new AccountLoginSession
                {
                    SessionId = v.SessionId, AccountId = v.AccountId, Runtime = v.Runtime, Method = v.Method, State = v.State, Reason = v.Reason,
                    VerificationUrl = v.VerificationUrl, UserCode = v.UserCode, NeedsCode = v.NeedsCode, Reused = v.Reused,
                    StartedUtc = v.StartedUtc, ExpiresUtc = v.State == AccountLoginStateEnum.Pending ? v.ExpiresUtc : null, CompletedUtc = v.CompletedUtc
                };
            }
        }

        private static void CreatePrivateDirectory(string path)
        {
            if (OperatingSystem.IsWindows())
            {
                Directory.CreateDirectory(path);
                return;
            }
            Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            // CreateDirectory leaves an existing folder's mode unchanged, and the umask narrows a new one; set it exactly.
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        private static JsonObject ReadAuthFile(string file)
        {
            FileInfo info = new FileInfo(file);
            if (!info.Exists) return new JsonObject();
            if (info.Length > _MaxAuthFileBytes) throw new AccountLoginException(ReasonAuthFileInvalid, 409, "The existing OpenCode credential file is too large to merge; it was left unchanged.");
            string existing = File.ReadAllText(file);
            if (String.IsNullOrWhiteSpace(existing)) return new JsonObject();
            try
            {
                if (JsonNode.Parse(existing) is JsonObject parsed) return parsed;
            }
            catch (JsonException)
            {
                // Handled below: an unreadable file is refused rather than replaced.
            }
            throw new AccountLoginException(ReasonAuthFileInvalid, 409, "The existing OpenCode credential file is not a JSON object; it was left unchanged.");
        }

        /// <summary>Write a file atomically with owner-only read and write permissions from the moment it exists.</summary>
        private static void WritePrivateFile(string path, string content)
        {
            string temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                FileStreamOptions options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
                if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                using (FileStream stream = new FileStream(temp, options))
                using (StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false)))
                {
                    writer.Write(content);
                }
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                File.Move(temp, path, true);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                try { if (File.Exists(temp)) File.Delete(temp); }
                catch (IOException) { /* Best effort; the partial file holds no complete credential path. */ }
                catch (UnauthorizedAccessException) { /* Best effort, as above. */ }
                throw new AccountLoginException(ReasonWriteFailed, 500, "The credential file could not be written.");
            }
        }

        #endregion

        #region Private-Classes

        private sealed class LoginEntry
        {
            public AccountLoginSession View { get; set; } = new AccountLoginSession();

            public IAccountLoginProcess? Process { get; set; }

            public CancellationTokenSource Lifetime { get; } = new CancellationTokenSource();

            public TaskCompletionSource<bool> PromptFound { get; } = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            public Task Completion { get; set; } = Task.CompletedTask;
        }

        #endregion
    }
}
