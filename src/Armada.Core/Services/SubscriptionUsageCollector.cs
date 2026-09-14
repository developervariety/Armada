namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Net.Http;
    using System.Net.Http.Headers;
    using System.Text;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Settings;

    /// <summary>Read subscription endpoints identified in CodexBar. Credentials remain in environment variables or runtime files.</summary>
    public static class SubscriptionUsageCollector
    {
        #region Private-Members

        private static readonly JsonSerializerOptions _Json = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        #endregion

        #region Public-Methods

        /// <summary>Collect Claude OAuth, Cursor cookie, or OpenCode Go API allowance without inference requests.</summary>
        public static async Task<ProviderUsageSnapshot> CollectAsync(UsageAccountSettings account, CancellationToken token = default)
        {
            using (HttpClientHandler handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
            using (HttpClient client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) })
                return await CollectAsync(account, client, token).ConfigureAwait(false);
        }

        internal static async Task<ProviderUsageSnapshot> CollectAsync(UsageAccountSettings account, HttpClient client, CancellationToken token = default)
        {
            string credential = await ReadCredentialAsync(account, token).ConfigureAwait(false);
            string url = account.Collector switch
            {
                "Claude" => "https://api.anthropic.com/api/oauth/usage",
                "Cursor" => "https://cursor.com/api/usage-summary",
                "OpenCodeGo" => "https://opencode.ai/zen/go/v1/usage",
                _ => throw new ArgumentException("unsupported_usage_collector")
            };
            using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, url))
            using (CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                request.Headers.UserAgent.ParseAdd("Armada/1.0");
                if (account.Collector == "Cursor") request.Headers.Add("Cookie", credential);
                else request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
                if (account.Collector == "Claude") request.Headers.Add("anthropic-beta", "oauth-2025-04-20");
                using (HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        DateTime? retry = null;
                        if ((int)response.StatusCode == 429)
                            retry = response.Headers.RetryAfter?.Date?.UtcDateTime
                                ?? DateTime.UtcNow.Add(response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMinutes(15));
                        throw new UsageCollectionException("usage_http_" + (int)response.StatusCode, retry);
                    }
                    using (Stream stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false))
                    {
                        string json = await ReadBoundedAsync(stream, timeout.Token).ConfigureAwait(false);
                        return Parse(account.Collector, json, DateTime.UtcNow);
                    }
                }
            }
        }

        /// <summary>Normalize provider-reported percentages, retaining separate pools and model-scoped windows.</summary>
        public static ProviderUsageSnapshot Parse(string collector, string json, DateTime observedUtc)
        {
            ProviderUsageSnapshot snapshot = new ProviderUsageSnapshot { ObservedUtc = observedUtc, Source = collector.ToLowerInvariant() + "_usage_api" };
            if (collector == "Claude")
            {
                ClaudeReply data = JsonSerializer.Deserialize<ClaudeReply>(json, _Json) ?? throw new InvalidDataException();
                Add(snapshot, "five_hour", data.FiveHour?.Utilization, data.FiveHour?.ResetsAt);
                Add(snapshot, "seven_day", data.SevenDay?.Utilization, data.SevenDay?.ResetsAt);
                if (data.SevenDayOauthApps != null) Add(snapshot, "seven_day_oauth_apps", data.SevenDayOauthApps.Utilization, data.SevenDayOauthApps.ResetsAt);
                if (data.SevenDayOpus != null) Add(snapshot, "seven_day_opus", data.SevenDayOpus.Utilization, data.SevenDayOpus.ResetsAt);
                if (data.SevenDaySonnet != null) Add(snapshot, "seven_day_sonnet", data.SevenDaySonnet.Utilization, data.SevenDaySonnet.ResetsAt);
                if (data.Limits != null)
                    foreach (ClaudeLimit limit in data.Limits)
                    {
                        if (limit == null || limit.IsActive == false || limit.Kind != "weekly_scoped") continue;
                        string scope = limit.Scope?.Model?.Id ?? limit.Scope?.Model?.DisplayName ?? "all";
                        Add(snapshot, "weekly_scoped/" + scope, limit.Percent, limit.ResetsAt);
                    }
            }
            else if (collector == "Cursor")
            {
                CursorReply data = JsonSerializer.Deserialize<CursorReply>(json, _Json) ?? throw new InvalidDataException();
                CursorPlan? plan = data.IndividualUsage?.Plan;
                // The total can be an average of independent pools. It is not a third binding limit.
                if (plan?.AutoPercentUsed != null || plan?.ApiPercentUsed != null)
                {
                    Add(snapshot, "cursor_models", plan.AutoPercentUsed, data.BillingCycleEnd);
                    Add(snapshot, "third_party", plan.ApiPercentUsed, data.BillingCycleEnd);
                }
                else
                {
                    double? used = plan?.TotalPercentUsed;
                    if (used == null && plan?.Limit > 0 && plan.Used.HasValue) used = 100 * plan.Used.Value / plan.Limit.Value;
                    Add(snapshot, "plan", used, data.BillingCycleEnd);
                }
            }
            else if (collector == "OpenCodeGo")
            {
                GoReply data = JsonSerializer.Deserialize<GoReply>(json, _Json) ?? throw new InvalidDataException();
                AddGo(snapshot, "rolling", data.Usage?.Rolling);
                AddGo(snapshot, "weekly", data.Usage?.Weekly);
                AddGo(snapshot, "monthly", data.Usage?.Monthly);
            }
            else throw new ArgumentException("unsupported_usage_collector");
            UsageRoutingService.ValidateSnapshot(snapshot);
            return snapshot;
        }

        #endregion

        #region Private-Methods

        private static void Add(ProviderUsageSnapshot snapshot, string name, double? used, string? reset)
        {
            DateTime? resetsUtc = null;
            if (reset != null)
            {
                if (!DateTimeOffset.TryParse(reset, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset parsed)) throw new InvalidDataException("invalid_usage_reset");
                resetsUtc = parsed.UtcDateTime;
            }
            if (used.HasValue && (!Double.IsFinite(used.Value) || used < 0)) throw new InvalidDataException("invalid_usage_percentage");
            snapshot.Windows.Add(new ProviderUsageWindow { Name = name, RemainingPercent = used.HasValue ? Math.Max(0, 100 - used.Value) : null, ResetsUtc = resetsUtc });
        }

        private static void AddGo(ProviderUsageSnapshot snapshot, string name, GoWindow? window)
        {
            string? reset = window?.ResetAt ?? window?.ResetsAt;
            if (reset == null && window?.ResetInSec != null) reset = snapshot.ObservedUtc.AddSeconds(window.ResetInSec.Value).ToString("O");
            Add(snapshot, name, window?.Percent, reset);
        }

        private static async Task<string> ReadCredentialAsync(UsageAccountSettings account, CancellationToken token)
        {
            string? credential = String.IsNullOrWhiteSpace(account.CredentialEnv) ? null : Environment.GetEnvironmentVariable(account.CredentialEnv);
            if (!String.IsNullOrWhiteSpace(credential)) return credential.Trim();
            string? path = account.CredentialFilePath;
            // An account login home owns its own login file; the shared default applies only without a home.
            if (String.IsNullOrWhiteSpace(path) && (account.Collector == "Claude" || account.Collector == "OpenCodeGo"))
                path = CaptainAccountLaunch.LoginFilePath(account);
            if (String.IsNullOrWhiteSpace(path) && account.Collector == "Claude")
                path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", ".credentials.json");
            if (String.IsNullOrWhiteSpace(path)) throw new UsageCollectionException("usage_credentials_not_configured");
            string text;
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                text = await ReadBoundedAsync(stream, token).ConfigureAwait(false);
            if (account.Collector == "Claude")
            {
                ClaudeCredentials? credentials = JsonSerializer.Deserialize<ClaudeCredentials>(text, _Json);
                credential = credentials?.ClaudeAiOauth?.AccessToken;
                if (credentials?.ClaudeAiOauth?.ExpiresAt <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) throw new UsageCollectionException("usage_credentials_expired");
            }
            else if (account.Collector == "OpenCodeGo" && text.TrimStart().StartsWith("{", StringComparison.Ordinal))
            {
                Dictionary<string, ApiCredential>? credentials = JsonSerializer.Deserialize<Dictionary<string, ApiCredential>>(text, _Json);
                credential = credentials != null && credentials.TryGetValue("opencode-go", out ApiCredential? entry) && entry?.Type == "api" ? entry.Key : null;
            }
            else credential = text.Trim();
            if (String.IsNullOrWhiteSpace(credential)) throw new UsageCollectionException("usage_credentials_unavailable");
            return credential;
        }

        private static async Task<string> ReadBoundedAsync(Stream stream, CancellationToken token)
        {
            byte[] bytes = new byte[65537];
            int count = 0;
            while (count < bytes.Length)
            {
                int read = await stream.ReadAsync(bytes.AsMemory(count), token).ConfigureAwait(false);
                if (read == 0) break;
                count += read;
            }
            if (count > 65536) throw new InvalidDataException("usage_response_too_large");
            return Encoding.UTF8.GetString(bytes, 0, count);
        }

        private sealed class ClaudeCredentials { public OAuthCredential? ClaudeAiOauth { get; set; } }
        private sealed class OAuthCredential { public string? AccessToken { get; set; } public long? ExpiresAt { get; set; } }
        private sealed class ApiCredential { public string? Type { get; set; } public string? Key { get; set; } }
        private sealed class ClaudeReply
        {
            [JsonPropertyName("five_hour")] public ClaudeWindow? FiveHour { get; set; }
            [JsonPropertyName("seven_day")] public ClaudeWindow? SevenDay { get; set; }
            [JsonPropertyName("seven_day_oauth_apps")] public ClaudeWindow? SevenDayOauthApps { get; set; }
            [JsonPropertyName("seven_day_opus")] public ClaudeWindow? SevenDayOpus { get; set; }
            [JsonPropertyName("seven_day_sonnet")] public ClaudeWindow? SevenDaySonnet { get; set; }
            public List<ClaudeLimit>? Limits { get; set; }
        }
        private sealed class ClaudeWindow
        {
            public double? Utilization { get; set; }
            [JsonPropertyName("resets_at")] public string? ResetsAt { get; set; }
        }
        private sealed class ClaudeLimit
        {
            public string? Kind { get; set; }
            public double? Percent { get; set; }
            [JsonPropertyName("resets_at")] public string? ResetsAt { get; set; }
            [JsonPropertyName("is_active")] public bool? IsActive { get; set; }
            public ClaudeScope? Scope { get; set; }
        }
        private sealed class ClaudeScope { public ClaudeModel? Model { get; set; } }
        private sealed class ClaudeModel { public string? Id { get; set; } [JsonPropertyName("display_name")] public string? DisplayName { get; set; } }
        private sealed class CursorReply { public string? BillingCycleEnd { get; set; } public CursorIndividual? IndividualUsage { get; set; } }
        private sealed class CursorIndividual { public CursorPlan? Plan { get; set; } }
        private sealed class CursorPlan
        {
            public double? AutoPercentUsed { get; set; }
            public double? ApiPercentUsed { get; set; }
            public double? TotalPercentUsed { get; set; }
            public double? Used { get; set; }
            public double? Limit { get; set; }
        }
        private sealed class GoReply { public GoUsage? Usage { get; set; } }
        private sealed class GoUsage { public GoWindow? Rolling { get; set; } public GoWindow? Weekly { get; set; } public GoWindow? Monthly { get; set; } }
        private sealed class GoWindow
        {
            public double? Percent { get; set; }
            public string? ResetAt { get; set; }
            public string? ResetsAt { get; set; }
            public double? ResetInSec { get; set; }
        }

        #endregion
    }
}
