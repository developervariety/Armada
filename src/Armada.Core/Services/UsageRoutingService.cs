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
        private UsageRoutingSettings? _LastSettings;
        private DateTime _NextRefreshUtc;

        #endregion

        #region Public-Methods

        /// <summary>Return the shared state for one Admiral settings instance.</summary>
        public static UsageRoutingService For(ArmadaSettings settings) => _Instances.GetValue(settings, _ => new UsageRoutingService());

        /// <summary>Validate configuration before publishing it to readers.</summary>
        public static void Validate(UsageRoutingSettings settings)
        {
            if (settings.RefreshIntervalMinutes < 1 || settings.RefreshIntervalMinutes > 60) throw new ArgumentException("Usage refresh interval must be between 1 and 60 minutes.");
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
            }
            HashSet<string> personas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, List<UsageRouteSettings>> pair in settings.PersonaRoutes)
            {
                if (String.IsNullOrWhiteSpace(pair.Key) || !personas.Add(PersonaCatalog.NormalizeName(pair.Key)) || pair.Value == null || pair.Value.Count == 0) throw new ArgumentException("Persona route lists must be nonempty and unique after normalization.");
                foreach (UsageRouteSettings route in pair.Value)
                    if (route == null || !ids.Contains(route.AccountId) || route.Models == null || route.Models.Any(String.IsNullOrWhiteSpace)) throw new ArgumentException("Each usage route must reference an account and a model list.");
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
                        foreach (string id in _AccountSources.Keys.Where(id => !retained.Contains(id)).ToList()) ForgetAccount(id);
                        foreach (UsageAccountSettings account in settings.Accounts)
                        {
                            string source = account.Collector + "\n" + account.CredentialEnv + "\n" + account.CredentialFilePath + "\n" + account.UsageFilePath
                                + "\n" + String.Join(",", account.CaptainIds) + "\n" + JsonSerializer.Serialize(account.WindowModels);
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
                    try
                    {
                        if (account.Collector != "File")
                        {
                            ProviderUsageSnapshot measured = account.Collector == "Codex"
                                ? await CodexUsageCollector.CollectAsync(token).ConfigureAwait(false)
                                : await SubscriptionUsageCollector.CollectAsync(account, token).ConfigureAwait(false);
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
                                int read = await stream.ReadAsync(buffer.AsMemory(count), token).ConfigureAwait(false);
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
                    catch (Exception ex) when (ex is FormatException || ex is IOException || ex is UnauthorizedAccessException || ex is JsonException || ex is ArgumentException || ex is System.ComponentModel.Win32Exception || ex is System.Net.Http.HttpRequestException || (ex is OperationCanceledException && !token.IsCancellationRequested))
                    {
                        lock (_StateLock)
                        {
                            _Errors[account.Id] = ex is UsageCollectionException failure ? failure.Code : "usage_snapshot_unavailable_or_invalid";
                            if (ex is UsageCollectionException limited && limited.RetryAfterUtc.HasValue) _RetryAfter[account.Id] = limited.RetryAfterUtc.Value;
                        }
                    }
                }).ConfigureAwait(false);
            }
            finally { _RefreshLock.Release(); }
        }

        private void ForgetAccount(string id)
        {
            _Snapshots.Remove(id); _Errors.Remove(id); _RetryAfter.Remove(id); _AccountSources.Remove(id);
            foreach (string key in _Conserving.Keys.Where(key => key.StartsWith(id + "\n", StringComparison.OrdinalIgnoreCase)).ToList()) _Conserving.Remove(key);
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

        /// <summary>Evaluate the applicable windows. A reset invalidates the old observation; it never invents a full allowance.</summary>
        public ProviderUsageStatus GetStatus(UsageAccountSettings account, string? model, DateTime now)
        {
            lock (_StateLock)
            {
                ProviderUsageSnapshot? snapshot = account.ManualSnapshot;
                if (account.Collector != "Manual") _Snapshots.TryGetValue(account.Id, out snapshot);
                ProviderUsageStatus result = new ProviderUsageStatus { AccountId = account.Id, ObservedUtc = snapshot?.ObservedUtc, Source = snapshot?.Source ?? "none", Windows = snapshot?.Windows ?? new List<ProviderUsageWindow>() };
                if (_Errors.TryGetValue(account.Id, out string? error)) result.CollectionError = error;
                if (account.OverrideState != null && account.OverrideUntilUtc > now)
                {
                    result.State = account.OverrideState;
                    result.Reason = "operator_override";
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
            UsageRoutingDecision result = new UsageRoutingDecision { Candidates = candidates, Reason = "usage_routing_disabled" };
            if (!settings.Enabled) return result;
            List<UsageRouteSettings>? routes = null;
            foreach (KeyValuePair<string, List<UsageRouteSettings>> pair in settings.PersonaRoutes)
                if (PersonaCatalog.Matches(pair.Key, mission.Persona)) { routes = pair.Value; break; }
            if (routes == null) settings.PersonaRoutes.TryGetValue("*", out routes);
            result.HasPersonaRoutes = routes != null;
            if (routes == null)
            {
                result.Candidates = new List<Captain>();
                result.Reason = "v2_persona_route_not_configured";
                return result;
            }
            List<Captain> normal = new List<Captain>();
            List<Captain> low = new List<Captain>();
            IEnumerable<UsageRouteSettings> ordered = routes;
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
                    if (state == "Exhausted" || (state == "Reserve" && !important)) continue;
                    if (state == "Low" && !important) low.Add(captain);
                    else normal.Add(captain);
                }
            }
            List<Captain> normalRetry = normal.Where(c => !MissionService.IsCaptainOnRetrySkipList(mission.RetrySkipCaptainIds, c.Id)).ToList();
            List<Captain> lowRetry = low.Where(c => !MissionService.IsCaptainOnRetrySkipList(mission.RetrySkipCaptainIds, c.Id)).ToList();
            if (normalRetry.Count + lowRetry.Count > 0) { normal = normalRetry; low = lowRetry; }
            result.Candidates = normal.Count > 0 ? normal : low;
            result.Reason = result.Candidates.Count == 0 ? "usage_reserve_exhaustion_or_account_capacity" : normal.Count == 0 ? "low_allowance_no_approved_normal_fallback" : "preferred_eligible_route_with_allowance";
            return result;
        }

        #endregion
    }
}
