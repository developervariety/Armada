namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Settings;

    /// <summary>Read subscription allowance through the supported Codex app-server RPC. Never starts a model turn.</summary>
    public static class CodexUsageCollector
    {
        #region Private-Members

        private static readonly JsonSerializerOptions _Json = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        #endregion

        #region Public-Methods

        /// <summary>
        /// Query a Codex account with a bounded process lifetime. An account with a login home is measured through its
        /// own CODEX_HOME, so separate Codex accounts report separate windows; otherwise the server user's login is used.
        /// </summary>
        public static Task<ProviderUsageSnapshot> CollectAsync(UsageAccountSettings? account, CancellationToken token = default)
        {
            return CollectAsync(account, "codex", token);
        }

        internal static ProcessStartInfo BuildStartInfo(UsageAccountSettings? account, string command)
        {
            ProcessStartInfo info = new ProcessStartInfo(command) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            info.ArgumentList.Add("app-server");
            info.ArgumentList.Add("--listen");
            info.ArgumentList.Add("stdio://");
            if (account != null && !String.IsNullOrWhiteSpace(account.HomeDirectory))
            {
                // A missing home would let Codex create an empty one and report an unauthenticated account as a provider error.
                if (!Directory.Exists(account.HomeDirectory)) throw new UsageCollectionException(CaptainAccountLaunch.ReasonHomeMissing);
                info.Environment[CaptainAccountLaunch.CodexHomeVariable] = account.HomeDirectory;
            }
            return info;
        }

        internal static async Task<ProviderUsageSnapshot> CollectAsync(UsageAccountSettings? account, string command, CancellationToken token)
        {
            using (CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            using (Process process = new Process())
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                process.StartInfo = BuildStartInfo(account, command);
                if (!process.Start()) throw new IOException("usage_collector_start_failed");
                Task drain = DrainAsync(process.StandardError, timeout.Token);
                try
                {
                    await process.StandardInput.WriteLineAsync("{\"id\":1,\"method\":\"initialize\",\"params\":{\"clientInfo\":{\"name\":\"armada_usage\",\"version\":\"1.0\"}}}").ConfigureAwait(false);
                    await ReadReplyAsync(process.StandardOutput, 1, timeout.Token).ConfigureAwait(false);
                    await process.StandardInput.WriteLineAsync("{\"method\":\"initialized\"}").ConfigureAwait(false);
                    await process.StandardInput.WriteLineAsync("{\"id\":2,\"method\":\"account/rateLimits/read\"}").ConfigureAwait(false);
                    string reply = await ReadReplyAsync(process.StandardOutput, 2, timeout.Token).ConfigureAwait(false);
                    return Parse(reply, DateTime.UtcNow);
                }
                finally
                {
                    if (!process.HasExited) process.Kill(true);
                    timeout.Cancel();
                    try { await drain.ConfigureAwait(false); }
                    catch (OperationCanceledException) { /* Expected when the bounded collector stops. */ }
                    await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
        }

        /// <summary>Normalize provider windows. Missing allowance never becomes a zero-usage measurement.</summary>
        public static ProviderUsageSnapshot Parse(string json, DateTime observedUtc)
        {
            Reply reply = JsonSerializer.Deserialize<Reply>(json, _Json) ?? throw new InvalidDataException();
            if (reply.Error != null || reply.Result == null) throw new InvalidDataException("usage_collector_provider_error");
            Dictionary<string, Bucket> buckets = reply.Result.RateLimitsByLimitId ?? new Dictionary<string, Bucket>();
            if (buckets.Count == 0 && reply.Result.RateLimits != null) buckets[reply.Result.RateLimits.LimitId ?? "account"] = reply.Result.RateLimits;
            ProviderUsageSnapshot snapshot = new ProviderUsageSnapshot { ObservedUtc = observedUtc, Source = "codex_app_server" };
            foreach (KeyValuePair<string, Bucket> pair in buckets)
            {
                if (pair.Value == null) throw new InvalidDataException("invalid_usage_bucket");
                AddWindow(snapshot, pair.Key + "/primary", pair.Value.Primary);
                AddWindow(snapshot, pair.Key + "/secondary", pair.Value.Secondary);
            }
            UsageRoutingService.ValidateSnapshot(snapshot);
            return snapshot;
        }

        #endregion

        #region Private-Methods

        private static void AddWindow(ProviderUsageSnapshot snapshot, string name, Window? window)
        {
            if (window == null) return;
            if (window.UsedPercent < 0) throw new InvalidDataException("invalid_usage_percentage");
            snapshot.Windows.Add(new ProviderUsageWindow
            {
                Name = name,
                RemainingPercent = window.UsedPercent.HasValue ? Math.Max(0, 100 - window.UsedPercent.Value) : null,
                ResetsUtc = window.ResetsAt.HasValue ? DateTimeOffset.FromUnixTimeSeconds(window.ResetsAt.Value).UtcDateTime : null
            });
        }

        private static async Task<string> ReadReplyAsync(StreamReader reader, int id, CancellationToken token)
        {
            char[] one = new char[1];
            System.Text.StringBuilder line = new System.Text.StringBuilder();
            int total = 0;
            while (total++ < 262144)
            {
                int read = await reader.ReadAsync(one.AsMemory(), token).ConfigureAwait(false);
                if (read == 0) throw new IOException("usage_collector_ended");
                if (one[0] != '\n') { line.Append(one[0]); continue; }
                string text = line.ToString();
                line.Clear();
                Reply? reply = JsonSerializer.Deserialize<Reply>(text, _Json);
                if (reply?.Id == id)
                {
                    if (reply.Error != null) throw new InvalidDataException("usage_collector_provider_error");
                    return text;
                }
            }
            throw new InvalidDataException("usage_collector_output_limit");
        }

        private static async Task DrainAsync(StreamReader reader, CancellationToken token)
        {
            char[] buffer = new char[4096];
            while (await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false) > 0) { }
        }

        private sealed class Reply
        {
            public int? Id { get; set; }
            public Result? Result { get; set; }
            public ProviderError? Error { get; set; }
        }
        private sealed class ProviderError { public int Code { get; set; } }
        private sealed class Result
        {
            public Bucket? RateLimits { get; set; }
            public Dictionary<string, Bucket>? RateLimitsByLimitId { get; set; }
        }
        private sealed class Bucket
        {
            public string? LimitId { get; set; }
            public Window? Primary { get; set; }
            public Window? Secondary { get; set; }
        }
        private sealed class Window
        {
            public double? UsedPercent { get; set; }
            public long? ResetsAt { get; set; }
        }

        #endregion
    }
}
