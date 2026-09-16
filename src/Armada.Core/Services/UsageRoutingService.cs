namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Runtime.CompilerServices;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Settings;

    /// <summary>Account allowance collection and preference-first conservation, shared by dispatch and preview.</summary>
    public sealed class UsageRoutingService
    {
        #region Private-Members

        private static readonly ConditionalWeakTable<ArmadaSettings, UsageRoutingService> _Instances = new ConditionalWeakTable<ArmadaSettings, UsageRoutingService>();
        private static readonly JsonSerializerOptions _Json = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        private readonly SemaphoreSlim _RefreshLock = new SemaphoreSlim(1, 1);
        private readonly object _StateLock = new object();
        private readonly Dictionary<string, ProviderUsageSnapshot> _Snapshots = new Dictionary<string, ProviderUsageSnapshot>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _Errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, bool> _Conserving = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DateTime> _RetryAfter = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _AccountSources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DateTime> _ExhaustedUntil = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, LoginProbeState> _LoginProbes = new Dictionary<string, LoginProbeState>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Task> _InFlight = new Dictionary<string, Task>(StringComparer.OrdinalIgnoreCase);
        private UsageRoutingSettings? _LastSettings;
        private DateTime _NextRefreshUtc;

        #endregion

        #region Public-Members

        /// <summary>Resolves the CLI a login probe runs for a runtime. Replaceable so tests can supply stub CLIs.</summary>
        public Func<Armada.Core.Enums.AgentRuntimeEnum, string> LoginProbeExecutable { get; set; } = AccountLoginProbe.DefaultExecutable;

        /// <summary>Reads a provider-measured account (every collector except Manual and File). Replaceable so tests can supply a fake provider.</summary>
        public Func<UsageAccountSettings, CancellationToken, Task<ProviderUsageSnapshot>> ProviderCollector { get; set; } = CollectFromProviderAsync;

        /// <summary>A hard refresh found an active provider retry-after, so the provider was not called.</summary>
        public const string ReasonRefreshRateLimited = "usage_refresh_rate_limited";

        /// <summary>A hard refresh read the account's usage.</summary>
        public const string ReasonRefreshed = "usage_refreshed";

        /// <summary>A hard refresh of a Manual account has nothing to collect.</summary>
        public const string ReasonRefreshManual = "usage_refresh_manual_snapshot";

        #endregion

        #region Public-Methods

        /// <summary>Return the shared state for one Admiral settings instance.</summary>
        public static UsageRoutingService For(ArmadaSettings settings) => _Instances.GetValue(settings, _ => new UsageRoutingService());

        /// <summary>Validate configuration before publishing it to readers.</summary>
        /// <param name="settings">Policy to validate.</param>
        /// <param name="accountsRoot">Optional Admiral account folder root; when set, Cursor key files must sit inside it.</param>
        public static void Validate(UsageRoutingSettings settings, string? accountsRoot = null)
        {
            if (settings.RefreshIntervalMinutes < 1 || settings.RefreshIntervalMinutes > 60) throw new ArgumentException("Usage refresh interval must be between 1 and 60 minutes.");
            if (settings.LoginProbeIntervalMinutes < 1 || settings.LoginProbeIntervalMinutes > 1440) throw new ArgumentException("Login probe interval must be between 1 and 1440 minutes.");
            if (settings.LoginProbeTimeoutSeconds < 1 || settings.LoginProbeTimeoutSeconds > 60) throw new ArgumentException("Login probe timeout must be between 1 and 60 seconds.");
            if (settings.Accounts == null || settings.PersonaRoutes == null) throw new ArgumentException("Usage accounts and persona routes cannot be null.");
            if (settings.Accounts.Count > 32) throw new ArgumentException("At most 32 usage accounts are supported.");
            if (settings.MonthlyBudget < 0) throw new ArgumentException("Monthly budget cannot be negative.");
            HashSet<string> ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> captains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (UsageAccountSettings account in settings.Accounts)
            {
                if (account == null || String.IsNullOrWhiteSpace(account.Id) || !ids.Add(account.Id)) throw new ArgumentException("Usage account IDs must be nonempty and unique.");
                if (account.CaptainIds == null || account.ReservedPersonas == null || account.ReservedPersonas.Any(String.IsNullOrWhiteSpace)) throw new ArgumentException("Account captain and persona lists cannot be null.");
                foreach (string id in account.CaptainIds)
                    if (String.IsNullOrWhiteSpace(id) || !captains.Add(id)) throw new ArgumentException("Each captain must belong to at most one usage account.");
                if (!Double.IsFinite(account.ReserveRemainingPercent) || !Double.IsFinite(account.LowRemainingPercent) || !Double.IsFinite(account.RecoveryRemainingPercent)
                    || account.ReserveRemainingPercent < 0 || account.LowRemainingPercent < account.ReserveRemainingPercent
                    || account.RecoveryRemainingPercent <= account.LowRemainingPercent || account.RecoveryRemainingPercent > 100)
                    throw new ArgumentException("Usage thresholds must satisfy 0 <= reserve <= low < recovery <= 100.");
                if (account.MaxAgeMinutes < 1 || account.MaxAgeMinutes > 10080 || account.ResetGraceMinutes < 0 || account.ResetGraceMinutes > 1440 || account.MaxConcurrentMissions < 0 || account.MonthlyCost < 0)
                    throw new ArgumentException("Usage age, reset grace, concurrency, or cost is outside its supported range.");
                if (account.UnknownUsagePolicy != "Allow" && account.UnknownUsagePolicy != "Conserve" && account.UnknownUsagePolicy != "Block") throw new ArgumentException("Unknown usage policy must be Allow, Conserve, or Block.");
                if (account.OverrideState != null && (!new[] { "Normal", "Low", "Reserve", "Exhausted" }.Contains(account.OverrideState) || !account.OverrideUntilUtc.HasValue || account.OverrideUntilUtc.Value.Kind != DateTimeKind.Utc)) throw new ArgumentException("Usage overrides need a valid state and expiry.");
                if (!new[] { "Manual", "File", "Codex", "Claude", "Cursor", "OpenCodeGo" }.Contains(account.Collector)) throw new ArgumentException("Unsupported usage collector.");
                if (account.WindowModels == null || account.WindowModels.Any(p => String.IsNullOrWhiteSpace(p.Key) || p.Value == null || p.Value.Any(String.IsNullOrWhiteSpace))) throw new ArgumentException("Usage window model mappings are invalid.");
                if (account.Collector == "File" && String.IsNullOrWhiteSpace(account.UsageFilePath)) throw new ArgumentException("File collector requires a usage snapshot path.");
                if (account.ManualSnapshot != null) ValidateSnapshot(account.ManualSnapshot);
                CaptainAccountLaunch.ValidateAccount(account, accountsRoot);
                if (account.Runtime.HasValue && !CollectorMatchesRuntime(account.Collector, account.Runtime.Value)) throw new ArgumentException("Usage account " + account.Id + " collector " + account.Collector + " does not measure runtime " + account.Runtime.Value + ".");
            }
            HashSet<string> personas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, List<UsageRouteSettings>> pair in settings.PersonaRoutes)
            {
                if (String.IsNullOrWhiteSpace(pair.Key) || !personas.Add(PersonaCatalog.NormalizeName(pair.Key)) || pair.Value == null || pair.Value.Count == 0) throw new ArgumentException("Persona route lists must be nonempty and unique after normalization.");
                foreach (UsageRouteSettings route in pair.Value)
                    if (route == null || !ids.Contains(route.AccountId) || route.Models == null || route.Models.Any(String.IsNullOrWhiteSpace) || route.Shapes == null || route.Shapes.Any(String.IsNullOrWhiteSpace)) throw new ArgumentException("Each usage route must reference an account and a model list, and its shape tags must be nonempty.");
            }
        }

        /// <summary>Validate a snapshot without accepting absent or invalid values as available capacity.</summary>
        public static void ValidateSnapshot(ProviderUsageSnapshot snapshot)
        {
            if (snapshot.ObservedUtc == default || snapshot.ObservedUtc.Kind != DateTimeKind.Utc || snapshot.ObservedUtc > DateTime.UtcNow.AddMinutes(1)) throw new ArgumentException("Usage observation must be a non-future UTC timestamp.");
            if (snapshot.Windows == null || snapshot.Windows.Count > 100 || String.IsNullOrWhiteSpace(snapshot.Source) || snapshot.Source.Length > 100) throw new ArgumentException("Usage snapshot source or windows are invalid.");
            HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ProviderUsageWindow window in snapshot.Windows)
            {
                if (window == null || String.IsNullOrWhiteSpace(window.Name) || !names.Add(window.Name) || window.Models == null) throw new ArgumentException("Usage windows must have unique names and model lists.");
                if (window.RemainingPercent.HasValue && (!Double.IsFinite(window.RemainingPercent.Value) || window.RemainingPercent < 0 || window.RemainingPercent > 100)) throw new ArgumentException("Usage remaining percentage must be between 0 and 100.");
                if (window.ResetsUtc.HasValue && window.ResetsUtc.Value.Kind != DateTimeKind.Utc) throw new ArgumentException("Usage reset must be UTC.");
            }
        }

        /// <summary>Refresh bounded snapshot files at most once per minute. Failures retain the last measurement and its original age.</summary>
        public async Task RefreshAsync(UsageRoutingSettings settings, CancellationToken token = default)
        {
            await _RefreshLock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (ReferenceEquals(settings, _LastSettings) && DateTime.UtcNow < _NextRefreshUtc) return;
                bool changed = !ReferenceEquals(settings, _LastSettings);
                _LastSettings = settings;
                _NextRefreshUtc = DateTime.UtcNow.AddMinutes(settings.RefreshIntervalMinutes);
                if (changed)
                {
                    lock (_StateLock)
                    {
                        HashSet<string> retained = new HashSet<string>(settings.Accounts.Select(a => a.Id), StringComparer.OrdinalIgnoreCase);
                        foreach (string id in _AccountSources.Keys.Where(id => !retained.Contains(id)).ToList()) { ForgetAccount(id); _ExhaustedUntil.Remove(id); }
                        foreach (string id in _LoginProbes.Keys.Where(id => !retained.Contains(id)).ToList()) _LoginProbes.Remove(id);
                        foreach (UsageAccountSettings account in settings.Accounts)
                        {
                            string source = SourceKey(account);
                            if (!_AccountSources.TryGetValue(account.Id, out string? previous) || source != previous) ForgetAccount(account.Id);
                            _AccountSources[account.Id] = source;
                        }
                    }
                }
                if (!settings.Enabled) return;
                await Parallel.ForEachAsync(settings.Accounts,
                    new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = token }, async (account, cancellation) =>
                {
                    if (account.Collector == "Manual") return;
                    lock (_StateLock)
                        if (_RetryAfter.TryGetValue(account.Id, out DateTime retryAt) && retryAt > DateTime.UtcNow) return;
                    await CollectSharedAsync(account).WaitAsync(token).ConfigureAwait(false);
                }).ConfigureAwait(false);
            }
            finally { _RefreshLock.Release(); }
        }

        /// <summary>Default provider read: the Codex collector for Codex accounts, the subscription collector otherwise.</summary>
        public static Task<ProviderUsageSnapshot> CollectFromProviderAsync(UsageAccountSettings account, CancellationToken token)
        {
            return account.Collector == "Codex" ? CodexUsageCollector.CollectAsync(account, token) : SubscriptionUsageCollector.CollectAsync(account, token);
        }

        /// <summary>
        /// Read one account's usage now, bypassing the refresh interval, and rerun its runtime login check. An active
        /// provider retry-after is still honoured: the provider is not called and the reason is
        /// <see cref="ReasonRefreshRateLimited"/>. Concurrent refreshes of the same account share one in-flight read.
        /// Returns null when the account is not in the policy.
        /// </summary>
        /// <param name="settings">Policy that holds the account.</param>
        /// <param name="accountId">Account identifier.</param>
        /// <param name="token">Cancellation token; cancelling stops waiting, not a read other callers share.</param>
        public async Task<UsageAccountRefreshResult?> RefreshAccountAsync(UsageRoutingSettings settings, string accountId, CancellationToken token = default)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            UsageAccountSettings? account = settings.Accounts?.FirstOrDefault(a => a != null && String.Equals(a.Id, accountId, StringComparison.Ordinal));
            if (account == null) return null;

            UsageAccountRefreshResult result = new UsageAccountRefreshResult { AccountId = account.Id };
            DateTime? retryAt = null;
            lock (_StateLock)
            {
                // Record the source this read belongs to, so the next timed refresh keeps it instead of discarding it as new.
                string source = SourceKey(account);
                if (_AccountSources.TryGetValue(account.Id, out string? previous) && previous != source) ForgetAccount(account.Id);
                _AccountSources[account.Id] = source;
                if (_RetryAfter.TryGetValue(account.Id, out DateTime limit) && limit > DateTime.UtcNow) retryAt = limit;
            }

            if (account.Collector == "Manual")
            {
                result.Reason = ReasonRefreshManual;
            }
            else if (retryAt.HasValue)
            {
                result.Reason = ReasonRefreshRateLimited;
                result.RetryAfterUtc = retryAt;
            }
            else
            {
                await CollectSharedAsync(account).WaitAsync(token).ConfigureAwait(false);
                lock (_StateLock)
                {
                    if (_Errors.TryGetValue(account.Id, out string? error))
                    {
                        result.Reason = error;
                        if (_RetryAfter.TryGetValue(account.Id, out DateTime limit) && limit > DateTime.UtcNow) result.RetryAfterUtc = limit;
                    }
                    else
                    {
                        result.Collected = true;
                        result.Reason = ReasonRefreshed;
                    }
                }
            }

            result.LoginProbeRerun = await RerunLoginProbeAsync(account, token).ConfigureAwait(false);
            result.Status = GetStatus(account, null, DateTime.UtcNow);
            return result;
        }

        /// <summary>Discard every measurement, error, retry time, provider hold, and login check kept for the account.</summary>
        public void ForgetAccountState(string accountId)
        {
            if (String.IsNullOrWhiteSpace(accountId)) return;
            lock (_StateLock)
            {
                ForgetAccount(accountId);
                _ExhaustedUntil.Remove(accountId);
                _LoginProbes.Remove(accountId);
            }
        }

        private static string SourceKey(UsageAccountSettings account)
        {
            return account.Collector + "\n" + account.CredentialEnv + "\n" + account.CredentialFilePath + "\n" + account.UsageFilePath
                + "\n" + account.Runtime + "\n" + account.HomeDirectory + "\n" + account.LaunchCredentialEnv + "\n" + account.LaunchCredentialFile
                + "\n" + String.Join(",", account.CaptainIds) + "\n" + JsonSerializer.Serialize(account.WindowModels);
        }

        /// <summary>Return the account's in-flight read, starting one when none runs.</summary>
        private Task CollectSharedAsync(UsageAccountSettings account)
        {
            lock (_StateLock)
            {
                if (_InFlight.TryGetValue(account.Id, out Task? running)) return running;
                // The read never takes a caller's token: another caller may be waiting on it.
                Task started = Task.Run(() => CollectOnceAsync(account));
                _InFlight[account.Id] = started;
                _ = started.ContinueWith(done =>
                {
                    lock (_StateLock)
                        if (_InFlight.TryGetValue(account.Id, out Task? current) && ReferenceEquals(current, done)) _InFlight.Remove(account.Id);
                }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
                return started;
            }
        }

        private async Task CollectOnceAsync(UsageAccountSettings account)
        {
            try
            {
                if (account.Collector != "File")
                {
                    ProviderUsageSnapshot measured = await ProviderCollector(account, CancellationToken.None).ConfigureAwait(false);
                    ApplyWindowModels(account, measured);
                    lock (_StateLock) { _Snapshots[account.Id] = measured; _Errors.Remove(account.Id); }
                    return;
                }
                using (FileStream stream = new FileStream(account.UsageFilePath!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, true))
                {
                    if (stream.Length > 65536) throw new InvalidDataException();
                    byte[] buffer = new byte[65537];
                    int count = 0;
                    while (count < buffer.Length)
                    {
                        int read = await stream.ReadAsync(buffer.AsMemory(count), CancellationToken.None).ConfigureAwait(false);
                        if (read == 0) break;
                        count += read;
                    }
                    if (count > 65536) throw new InvalidDataException();
                    ProviderUsageSnapshot snapshot = JsonSerializer.Deserialize<ProviderUsageSnapshot>(buffer.AsSpan(0, count), _Json) ?? throw new InvalidDataException();
                    ValidateSnapshot(snapshot);
                    ApplyWindowModels(account, snapshot);
                    lock (_StateLock)
                    {
                        if (!_Snapshots.TryGetValue(account.Id, out ProviderUsageSnapshot? previous) || snapshot.ObservedUtc >= previous.ObservedUtc) _Snapshots[account.Id] = snapshot;
                        _Errors.Remove(account.Id);
                    }
                }
            }
            catch (Exception ex) when (ex is FormatException || ex is IOException || ex is UnauthorizedAccessException || ex is JsonException || ex is ArgumentException || ex is System.ComponentModel.Win32Exception || ex is System.Net.Http.HttpRequestException || ex is OperationCanceledException)
            {
                lock (_StateLock)
                {
                    _Errors[account.Id] = ex is UsageCollectionException failure ? failure.Code : "usage_snapshot_unavailable_or_invalid";
                    if (ex is UsageCollectionException limited && limited.RetryAfterUtc.HasValue) _RetryAfter[account.Id] = limited.RetryAfterUtc.Value;
                }
            }
        }

        /// <summary>
        /// Start a fresh runtime login check for the account and wait for it, bounded by the probe timeout. A check that is
        /// already running is awaited instead of starting a second one. Returns false when the account has no status check.
        /// </summary>
        private async Task<bool> RerunLoginProbeAsync(UsageAccountSettings account, CancellationToken token)
        {
            if (!CaptainAccountLaunch.HasLaunchIdentity(account) || !AccountLoginProbe.HasStatusCommand(account.Runtime!.Value)) return false;
            // A missing login file blocks before any status command, so no probe would start.
            if (CaptainAccountLaunch.CheckReadiness(account) != null) return false;
            bool running;
            lock (_StateLock)
            {
                running = _LoginProbes.TryGetValue(account.Id, out LoginProbeState? existing) && existing.Running;
                if (!running) _LoginProbes.Remove(account.Id);
            }
            if (!running) GetLoginProblem(account, DateTime.UtcNow);
            Task? completion;
            lock (_StateLock) completion = _LoginProbes.TryGetValue(account.Id, out LoginProbeState? state) ? state.Completion : null;
            if (completion == null) return false;
            TimeSpan wait = TimeSpan.FromSeconds((_LastSettings?.LoginProbeTimeoutSeconds ?? 10) + 5);
            try
            {
                await completion.WaitAsync(wait, token).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // The check keeps running and records its own result; the returned status shows the last finished check.
            }
            return true;
        }

        private void ForgetAccount(string id)
        {
            _Snapshots.Remove(id); _Errors.Remove(id); _RetryAfter.Remove(id); _AccountSources.Remove(id);
            foreach (string key in _Conserving.Keys.Where(key => key.StartsWith(id + "\n", StringComparison.OrdinalIgnoreCase)).ToList()) _Conserving.Remove(key);
        }

        private void StartLoginProbe(UsageAccountSettings account, LoginProbeState state, TimeSpan timeout)
        {
            UsageAccountSettings snapshot = new UsageAccountSettings { Id = account.Id, Runtime = account.Runtime, HomeDirectory = account.HomeDirectory };
            string executable = LoginProbeExecutable(account.Runtime!.Value);
            Task probe = Task.Run(async () =>
            {
                string? reason;
                try
                {
                    reason = await AccountLoginProbe.RunAsync(snapshot, executable, timeout).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException || ex is InvalidOperationException || ex is UnauthorizedAccessException || ex is System.ComponentModel.Win32Exception)
                {
                    // Named, not silent: an unexpected probe fault blocks the account with its own reason.
                    reason = AccountLoginProbe.ReasonProbeFailed;
                }
                lock (_StateLock)
                {
                    state.Reason = reason;
                    state.CheckedUtc = DateTime.UtcNow;
                    state.Running = false;
                }
            });
            lock (_StateLock) state.Completion = probe;
        }

        private static bool CollectorMatchesRuntime(string collector, Armada.Core.Enums.AgentRuntimeEnum runtime)
        {
            return collector switch
            {
                "Codex" => runtime == Armada.Core.Enums.AgentRuntimeEnum.Codex,
                "Claude" => runtime == Armada.Core.Enums.AgentRuntimeEnum.ClaudeCode,
                "Cursor" => runtime == Armada.Core.Enums.AgentRuntimeEnum.Cursor,
                "OpenCodeGo" => runtime == Armada.Core.Enums.AgentRuntimeEnum.OpenCode,
                _ => true
            };
        }

        private static void ApplyWindowModels(UsageAccountSettings account, ProviderUsageSnapshot snapshot)
        {
            foreach (ProviderUsageWindow window in snapshot.Windows)
                window.Models = new List<string>(GetWindowModels(account, window));
        }

        private static List<string> GetWindowModels(UsageAccountSettings account, ProviderUsageWindow window)
        {
            foreach (KeyValuePair<string, List<string>> mapping in account.WindowModels)
                if (String.Equals(mapping.Key, window.Name, StringComparison.OrdinalIgnoreCase)) return mapping.Value;
            return window.Models;
        }

        /// <summary>
        /// Mark a whole account Exhausted until the provider's retry time, after one of its captains failed on a quota,
        /// billing, or authentication signal. Every captain on the account shares the same allowance and login, so the
        /// next one would fail the same way. An operator override still wins; a later mark never shortens an earlier one.
        /// </summary>
        public void MarkAccountExhausted(string accountId, DateTime untilUtc)
        {
            if (String.IsNullOrWhiteSpace(accountId)) throw new ArgumentException("Account ID is required.", nameof(accountId));
            DateTime until = untilUtc.Kind == DateTimeKind.Utc ? untilUtc : DateTime.SpecifyKind(untilUtc.ToUniversalTime(), DateTimeKind.Utc);
            lock (_StateLock)
                if (!_ExhaustedUntil.TryGetValue(accountId, out DateTime existing) || existing < until) _ExhaustedUntil[accountId] = until;
        }

        /// <summary>
        /// Return the account's login problem, or null when its login is usable or it has no login binding. The file or
        /// variable check runs first. For runtimes with a status command, the last probe result is returned and a stale
        /// or missing one starts a background probe; this call never waits for a probe, so a scheduler tick is never
        /// blocked. Before the first probe finishes, only the file check applies.
        /// </summary>
        public string? GetLoginProblem(UsageAccountSettings account, DateTime now)
        {
            if (account == null) throw new ArgumentNullException(nameof(account));
            string? fileProblem = CaptainAccountLaunch.CheckReadiness(account);
            if (fileProblem != null || !CaptainAccountLaunch.HasLaunchIdentity(account) || !AccountLoginProbe.HasStatusCommand(account.Runtime!.Value)) return fileProblem;
            UsageRoutingSettings? settings = _LastSettings;
            TimeSpan interval = TimeSpan.FromMinutes(settings?.LoginProbeIntervalMinutes ?? 10);
            TimeSpan timeout = TimeSpan.FromSeconds(settings?.LoginProbeTimeoutSeconds ?? 10);
            string source = account.Runtime + "\n" + account.HomeDirectory;
            LoginProbeState state;
            bool start = false;
            lock (_StateLock)
            {
                if (!_LoginProbes.TryGetValue(account.Id, out LoginProbeState? existing) || existing.Source != source)
                {
                    existing = new LoginProbeState { Source = source };
                    _LoginProbes[account.Id] = existing;
                }
                state = existing;
                if (!state.Running && (!state.CheckedUtc.HasValue || state.CheckedUtc.Value.Add(interval) <= now))
                {
                    state.Running = true;
                    start = true;
                }
            }
            if (start) StartLoginProbe(account, state, timeout);
            lock (_StateLock) return state.Reason;
        }

        /// <summary>
        /// Discard the account's cached login probe result, so the next login check starts a fresh probe. Called after a
        /// login completes, so a stale "expired" result does not outlive the new login.
        /// </summary>
        public void InvalidateLoginProbe(string accountId)
        {
            if (String.IsNullOrWhiteSpace(accountId)) return;
            lock (_StateLock)
            {
                // A running probe keeps its state object; replacing the entry makes its late result land nowhere.
                _LoginProbes.Remove(accountId);
            }
        }

        /// <summary>When the account's last runtime login status probe finished, or null before any probe.</summary>
        public DateTime? GetLoginCheckedUtc(string accountId)
        {
            lock (_StateLock) return _LoginProbes.TryGetValue(accountId, out LoginProbeState? state) ? state.CheckedUtc : null;
        }

        /// <summary>Evaluate the applicable windows. A reset invalidates the old observation; it never invents a full allowance.</summary>
        public ProviderUsageStatus GetStatus(UsageAccountSettings account, string? model, DateTime now)
        {
            // A missing or rejected login blocks before any allowance question: the captain cannot run at all.
            string? loginProblem = GetLoginProblem(account, now);
            DateTime? loginCheckedUtc = GetLoginCheckedUtc(account.Id);
            lock (_StateLock)
            {
                ProviderUsageSnapshot? snapshot = account.ManualSnapshot;
                if (account.Collector != "Manual") _Snapshots.TryGetValue(account.Id, out snapshot);
                ProviderUsageStatus result = new ProviderUsageStatus { AccountId = account.Id, ObservedUtc = snapshot?.ObservedUtc, Source = snapshot?.Source ?? "none", Windows = snapshot?.Windows ?? new List<ProviderUsageWindow>() };
                result.Runtime = account.Runtime?.ToString();
                result.LoginCheckedUtc = loginCheckedUtc;
                if (_Errors.TryGetValue(account.Id, out string? error)) result.CollectionError = error;
                if (loginProblem != null)
                {
                    result.State = "Exhausted";
                    result.Reason = loginProblem;
                    return result;
                }
                if (account.OverrideState != null && account.OverrideUntilUtc > now)
                {
                    result.State = account.OverrideState;
                    result.Reason = "operator_override";
                    return result;
                }
                if (_ExhaustedUntil.TryGetValue(account.Id, out DateTime exhaustedUntil) && exhaustedUntil > now)
                {
                    result.State = "Exhausted";
                    result.Reason = "account_provider_failure";
                    result.ExhaustedUntilUtc = exhaustedUntil;
                    return result;
                }
                bool stale = snapshot == null || snapshot.ObservedUtc > now || snapshot.ObservedUtc.AddMinutes(account.MaxAgeMinutes) <= now;
                int severity = 0;
                bool unknown = stale;
                bool any = false;
                if (snapshot != null)
                {
                    foreach (ProviderUsageWindow window in snapshot.Windows)
                    {
                        List<string> windowModels = GetWindowModels(account, window);
                        if (model != null && windowModels.Count > 0 && !windowModels.Contains(model, StringComparer.OrdinalIgnoreCase)) continue;
                        any = true;
                        if (stale || window.ResetsUtc <= now || !window.RemainingPercent.HasValue) { unknown = true; continue; }
                        double remaining = window.RemainingPercent.Value;
                        string key = account.Id + "\n" + window.Name;
                        bool conserving = _Conserving.TryGetValue(key, out bool previous) && previous;
                        conserving = remaining <= account.LowRemainingPercent || (conserving && remaining < account.RecoveryRemainingPercent);
                        _Conserving[key] = conserving;
                        bool resetSoon = window.ResetsUtc.HasValue && window.ResetsUtc.Value <= now.AddMinutes(account.ResetGraceMinutes);
                        int current = remaining <= 0 ? 4 : remaining <= account.ReserveRemainingPercent ? 3 : conserving && !resetSoon ? 2 : 0;
                        severity = Math.Max(severity, current);
                    }
                }
                unknown |= !any;
                result.State = severity == 4 ? "Exhausted" : severity == 3 ? "Reserve" : severity == 2 ? "Low" : unknown ? "Unknown" : "Normal";
                // An unknown window is also binding; a known low window must not hide an unknown-data block.
                if (unknown && account.UnknownUsagePolicy == "Block" && severity < 4) result.State = "Unknown";
                if (unknown && account.UnknownUsagePolicy == "Conserve" && severity < 2) result.State = "Unknown";
                result.Reason = unknown ? "required_usage_window_unknown_or_stale" : "measured_usage_windows";
                return result;
            }
        }

        /// <summary>Apply the same persona and minimum tier constraints in preview and dispatch.</summary>
        public static List<Captain> Eligible(ModelTierSettings settings, Mission mission, IEnumerable<Captain> candidates)
        {
            return candidates.Where(c => MissionService.CaptainSatisfiesPreferredRouting(c, mission.Persona, mission.PreferredModel, settings)
                && (!settings.IsSpecialistPersona(mission.Persona) || !settings.HasConfiguredTierMembership
                    || String.Equals(PreferredModelTierSelector.ClassifyModel(c.Model, settings), PreferredModelTierSelector.HighTier, StringComparison.OrdinalIgnoreCase))).ToList();
        }

        /// <summary>Filter approved candidates without ever sorting by remaining percentage.</summary>
        public UsageRoutingDecision Select(UsageRoutingSettings settings, Mission mission, List<Captain> candidates, IReadOnlyCollection<string> busyCaptainIds, DateTime now)
        {
            return Select(settings, mission, candidates, busyCaptainIds, now, RoutingHint.None());
        }

        /// <summary>
        /// Filter approved candidates without ever sorting by remaining percentage, applying an optional
        /// D16 <c>routing_hint</c> shape hint that reorders the already-approved routes.
        /// </summary>
        /// <remarks>
        /// The hint is advisory and additive. It reorders the route list before eligibility is applied,
        /// so every hard constraint — account state (Reserve, Exhausted, Low, Unknown), the reserved
        /// persona path, the tier constraint, and the route's own model list — still runs unchanged per
        /// route. A reordered route that is not eligible is skipped exactly as it would have been. The
        /// reserved-persona path is never reordered at all. When the hint is <see cref="RoutingHint.None"/>
        /// (the default overload, and whenever the decision is Off, unavailable, or below threshold), the
        /// plain V2 list order applies.
        /// </remarks>
        /// <param name="settings">Usage routing settings.</param>
        /// <param name="mission">The mission being routed.</param>
        /// <param name="candidates">The usage-eligible captains.</param>
        /// <param name="busyCaptainIds">Captains currently working, for account concurrency.</param>
        /// <param name="now">The evaluation time.</param>
        /// <param name="hint">The D16 shape hint, or <see cref="RoutingHint.None"/>.</param>
        /// <returns>The routing decision.</returns>
        public UsageRoutingDecision Select(UsageRoutingSettings settings, Mission mission, List<Captain> candidates, IReadOnlyCollection<string> busyCaptainIds, DateTime now, RoutingHint hint)
        {
            UsageRoutingDecision result = new UsageRoutingDecision { Candidates = candidates, Reason = "usage_routing_disabled" };
            if (!settings.Enabled) return result;
            List<UsageRouteSettings>? routes = null;
            foreach (KeyValuePair<string, List<UsageRouteSettings>> pair in settings.PersonaRoutes)
                if (PersonaCatalog.Matches(pair.Key, mission.Persona)) { routes = pair.Value; break; }
            if (routes == null) settings.PersonaRoutes.TryGetValue("*", out routes);
            result.HasPersonaRoutes = routes != null;
            if (routes == null)
            {
                // No route governs this persona (no persona-specific route and no "*" default), so Smart
                // Routing does not narrow the field: keep the candidates the legacy selector already
                // approved. Enabling Smart Routing with no configured routes is then a safe no-op rather
                // than a blanket assignment block for every ungoverned persona.
                result.Candidates = candidates;
                result.Reason = "v2_no_route_pass_through";
                return result;
            }
            List<Captain> normal = new List<Captain>();
            List<Captain> low = new List<Captain>();
            string? accountBlockReason = null;
            IEnumerable<UsageRouteSettings> ordered = ApplyRoutingHint(routes, settings, mission, hint, result);
            foreach (UsageRouteSettings route in ordered)
            {
                UsageAccountSettings account = settings.Accounts.First(a => String.Equals(a.Id, route.AccountId, StringComparison.OrdinalIgnoreCase));
                if (account.MaxConcurrentMissions > 0 && account.CaptainIds.Count(id => busyCaptainIds.Contains(id)) >= account.MaxConcurrentMissions) continue;
                bool important = account.ReservedPersonas.Any(p => PersonaCatalog.Matches(p, mission.Persona)) || (account.ReservedPriorityAtOrAbove.HasValue && mission.Priority <= account.ReservedPriorityAtOrAbove.Value);
                foreach (Captain captain in candidates.OrderBy(c => route.Models.Count == 0 ? 0 : route.Models.FindIndex(m => String.Equals(m, c.Model, StringComparison.OrdinalIgnoreCase))).ThenBy(c => c.Id, StringComparer.Ordinal))
                {
                    if (!account.CaptainIds.Contains(captain.Id, StringComparer.OrdinalIgnoreCase) || (route.Models.Count > 0 && !route.Models.Contains(captain.Model ?? "", StringComparer.OrdinalIgnoreCase))) continue;
                    ProviderUsageStatus status = GetStatus(account, captain.Model, now);
                    string state = status.State == "Unknown" ? account.UnknownUsagePolicy == "Block" ? "Exhausted" : account.UnknownUsagePolicy == "Conserve" ? "Low" : "Normal" : status.State;
                    // A login problem or provider hold names the account, not its allowance; keep the first such code so a
                    // mission that waits says why instead of reading as a generic allowance shortage.
                    if (state == "Exhausted" && accountBlockReason == null && status.Reason != null && status.Reason.StartsWith("account_", StringComparison.Ordinal))
                        accountBlockReason = status.Reason;
                    if (state == "Exhausted" || (state == "Reserve" && !important)) continue;
                    if (state == "Low" && !important) low.Add(captain);
                    else normal.Add(captain);
                }
            }
            List<Captain> normalRetry = normal.Where(c => !MissionService.IsCaptainOnRetrySkipList(mission.RetrySkipCaptainIds, c.Id)).ToList();
            List<Captain> lowRetry = low.Where(c => !MissionService.IsCaptainOnRetrySkipList(mission.RetrySkipCaptainIds, c.Id)).ToList();
            if (normalRetry.Count + lowRetry.Count > 0) { normal = normalRetry; low = lowRetry; }
            result.Candidates = normal.Count > 0 ? normal : low;
            result.Reason = result.Candidates.Count == 0 ? accountBlockReason ?? "usage_reserve_exhaustion_or_account_capacity" : normal.Count == 0 ? "low_allowance_no_approved_normal_fallback" : "preferred_eligible_route_with_allowance";
            return result;
        }

        /// <summary>The route tag that marks an account/model as tolerant of policy-sensitive diagnostic work.</summary>
        public const string PolicyTolerantTag = "policy-tolerant";

        /// <summary>
        /// Reorder the persona's approved routes by the D16 shape hint, without changing eligibility.
        /// </summary>
        /// <remarks>
        /// A stable reorder puts the routes the hint prefers first, so the "first eligible" default then
        /// falls on a shape-matched or policy-tolerant route when one is eligible, and on the original
        /// list order otherwise. The reserved-persona path is never reordered: when the mission is
        /// reserved on any of its routes, the plain list order is kept. Every hard V2 constraint is still
        /// applied per route by the caller after this reorder.
        /// </remarks>
        private static IReadOnlyList<UsageRouteSettings> ApplyRoutingHint(
            List<UsageRouteSettings> routes,
            UsageRoutingSettings settings,
            Mission mission,
            RoutingHint hint,
            UsageRoutingDecision result)
        {
            if (!hint.Applied) return routes;

            // Reserved personas (and reserved-priority missions) are never affected by the hint. Their
            // route order is left exactly as configured.
            bool reservedAnywhere = routes.Any(route =>
            {
                UsageAccountSettings? account = settings.Accounts.FirstOrDefault(a => String.Equals(a.Id, route.AccountId, StringComparison.OrdinalIgnoreCase));
                if (account == null) return false;
                return account.ReservedPersonas.Any(p => PersonaCatalog.Matches(p, mission.Persona))
                    || (account.ReservedPriorityAtOrAbove.HasValue && mission.Priority <= account.ReservedPriorityAtOrAbove.Value);
            });
            if (reservedAnywhere)
            {
                result.RoutingHintOutcome = "reserved_persona_unchanged";
                return routes;
            }

            if (hint.PreferPolicyTolerant)
            {
                bool anyTolerant = routes.Any(route => RouteHasShape(route, PolicyTolerantTag));
                if (!anyTolerant)
                {
                    // No policy-tolerant route is configured for this persona; fall back to V2 default.
                    result.RoutingHintOutcome = "no_tolerant_route";
                    return routes;
                }
                result.RoutingHintOutcome = "policy_tolerant";
                return StableOrderPreferring(routes, route => RouteHasShape(route, PolicyTolerantTag));
            }

            if (!String.IsNullOrWhiteSpace(hint.ChosenShape))
            {
                bool anyMatch = routes.Any(route => RouteHasShape(route, hint.ChosenShape!));
                if (!anyMatch)
                {
                    result.RoutingHintOutcome = "no_shape_match";
                    return routes;
                }
                result.RoutingHintOutcome = "applied_shape:" + hint.ChosenShape;
                return StableOrderPreferring(routes, route => RouteHasShape(route, hint.ChosenShape!));
            }

            return routes;
        }

        private static bool RouteHasShape(UsageRouteSettings route, string shape)
        {
            if (route.Shapes == null || route.Shapes.Count == 0) return false;
            return route.Shapes.Any(tag => String.Equals(tag, shape, StringComparison.OrdinalIgnoreCase));
        }

        private static IReadOnlyList<UsageRouteSettings> StableOrderPreferring(List<UsageRouteSettings> routes, Func<UsageRouteSettings, bool> preferred)
        {
            // Enumerable.OrderBy is a stable sort, so routes that share a key keep their configured order.
            return routes.OrderBy(route => preferred(route) ? 0 : 1).ToList();
        }

        #endregion

        #region Private-Classes

        private sealed class LoginProbeState
        {
            public string Source { get; set; } = String.Empty;
            public string? Reason { get; set; }
            public DateTime? CheckedUtc { get; set; }
            public bool Running { get; set; }
            public Task? Completion { get; set; }
        }

        #endregion
    }
}
