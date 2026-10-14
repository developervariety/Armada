namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Security.Cryptography;
    using System.Text;
    using System.Text.RegularExpressions;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Models;

    /// <summary>Validates and imports tagged synthetic usage without touching existing real activity or invoking models.</summary>
    public static class TokenUsageSyntheticImporter
    {
        /// <summary>Import a batch for the authenticated administrator, validating all rows before writes.</summary>
        /// <param name="storage">Usage storage.</param>
        /// <param name="request">Simulation data.</param>
        /// <param name="tenantId">Authenticated tenant.</param>
        /// <param name="userId">Authenticated user.</param>
        /// <param name="now">Current UTC time.</param>
        /// <param name="token">Cancellation.</param>
        /// <returns>Import receipt.</returns>
        /// <exception cref="ArgumentException">Invalid batch or record.</exception>
        /// <exception cref="InvalidOperationException">Batch reused with different content.</exception>
        public static async Task<TokenUsageSyntheticResult> ImportAsync(ITokenUsageMethods storage, TokenUsageSyntheticRequest request, string tenantId, string userId, DateTime now, CancellationToken token = default)
        {
            if (request == null || !Regex.IsMatch(request.BatchId ?? "", "^[A-Za-z0-9_-]{1,80}$")) throw new ArgumentException("BatchId must contain 1-80 letters, digits, underscores or hyphens.");
            if (request.Records == null || request.Records.Count < 1 || request.Records.Count > 500) throw new ArgumentException("Supply 1-500 synthetic records per batch.");
            List<TokenUsageRecord> records = new List<TokenUsageRecord>();
            for (int i = 0; i < request.Records.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                TokenUsageSyntheticRecord row = request.Records[i];
                if (row == null || String.IsNullOrWhiteSpace(row.Model) || row.Model.Length > 128 || (row.Runtime?.Length ?? 0) > 64) throw new ArgumentException("Each synthetic record requires a model of at most 128 characters and a bounded runtime.");
                DateTime utc = row.CreatedUtc.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(row.CreatedUtc, DateTimeKind.Utc) : row.CreatedUtc.ToUniversalTime();
                if (utc < now.AddDays(-366) || utc > now) throw new ArgumentException("Synthetic timestamps must be in the past 366 days, not the future.");
                if (row.InputTokens < 0 || row.OutputTokens < 0 || row.CachedTokens < 0 || row.InputTokens > 1000000000 || row.OutputTokens > 1000000000 || row.CachedTokens > 1000000000) throw new ArgumentException("Synthetic token counts must be between 0 and 1,000,000,000.");
                byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(tenantId + ":" + userId + ":" + request.BatchId + ":" + i));
                records.Add(new TokenUsageRecord
                {
                    Id = "tku_" + Convert.ToHexString(hash).Substring(0, 24).ToLowerInvariant(),
                    TenantId = tenantId, UserId = userId, Model = row.Model.Trim(), Runtime = row.Runtime,
                    Source = "synthetic", SourceId = "synthetic:" + request.BatchId,
                    CreatedUtc = utc, InputTokens = row.InputTokens, OutputTokens = row.OutputTokens, CachedTokens = row.CachedTokens,
                    TotalTokens = row.InputTokens + row.OutputTokens, Estimated = true
                });
            }
            List<TokenUsageRecord> missing = new List<TokenUsageRecord>();
            foreach (TokenUsageRecord record in records)
            {
                token.ThrowIfCancellationRequested();
                TokenUsageRecord? old = await storage.ReadAsync(record.Id, null, token).ConfigureAwait(false);
                if (old == null) missing.Add(record);
                else if (old.TenantId != tenantId || old.UserId != userId || old.Source != "synthetic" || old.SourceId != record.SourceId || old.CreatedUtc != record.CreatedUtc || old.Model != record.Model || old.Runtime != record.Runtime || old.InputTokens != record.InputTokens || old.OutputTokens != record.OutputTokens || old.CachedTokens != record.CachedTokens || old.TotalTokens != record.TotalTokens || !old.Estimated)
                    throw new TokenUsageSyntheticConflictException();
            }
            int created = 0;
            foreach (TokenUsageRecord record in missing)
            {
                token.ThrowIfCancellationRequested();
                try { await storage.CreateAsync(record, token).ConfigureAwait(false); created++; }
                catch (Exception) when (!token.IsCancellationRequested)
                {
                    // Concurrent identical requests may create the same id between preflight and insertion.
                    TokenUsageRecord? saved = await storage.ReadAsync(record.Id, null, token).ConfigureAwait(false);
                    if (saved == null || saved.TenantId != record.TenantId || saved.UserId != record.UserId || saved.SourceId != record.SourceId || saved.Source != record.Source || saved.CreatedUtc != record.CreatedUtc || saved.Model != record.Model || saved.Runtime != record.Runtime || saved.InputTokens != record.InputTokens || saved.OutputTokens != record.OutputTokens || saved.CachedTokens != record.CachedTokens || saved.TotalTokens != record.TotalTokens || !saved.Estimated) throw;
                }
            }
            return new TokenUsageSyntheticResult { BatchId = request.BatchId!, SourceId = "synthetic:" + request.BatchId, Created = created, Existing = records.Count - created, FromUtc = records.Min(r => r.CreatedUtc), ToUtc = records.Max(r => r.CreatedUtc), TotalTokens = records.Sum(r => r.TotalTokens) };
        }
    }
}
