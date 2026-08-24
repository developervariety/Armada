namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using Armada.Core.Models;

    /// <summary>
    /// Merges legacy token-usage events with records from the token_usage table without double-counting
    /// events that were also written to the table by the current capture path.
    /// </summary>
    public static class TokenUsageCompatibility
    {
        #region Public-Methods

        /// <summary>
        /// Add legacy mission token-usage events that do not already have a matching table record.
        /// </summary>
        /// <param name="records">Current token-usage table records.</param>
        /// <param name="events">Legacy mission.token_usage events.</param>
        /// <param name="jsonOptions">JSON options used for event payloads.</param>
        /// <returns>The combined records.</returns>
        public static List<TokenUsageRecord> MergeLegacyEvents(
            List<TokenUsageRecord> records,
            List<ArmadaEvent> events,
            JsonSerializerOptions jsonOptions)
        {
            if (records == null) throw new ArgumentNullException(nameof(records));
            if (events == null) throw new ArgumentNullException(nameof(events));
            if (jsonOptions == null) throw new ArgumentNullException(nameof(jsonOptions));

            List<TokenUsageRecord> merged = new List<TokenUsageRecord>(records);
            Dictionary<string, int> availableTableRecords = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (TokenUsageRecord record in records)
            {
                string key = BuildMatchKey(record);
                availableTableRecords.TryGetValue(key, out int count);
                availableTableRecords[key] = count + 1;
            }

            foreach (ArmadaEvent armadaEvent in events)
            {
                if (!String.Equals(armadaEvent.EventType, "mission.token_usage", StringComparison.Ordinal) ||
                    String.IsNullOrWhiteSpace(armadaEvent.Payload))
                    continue;

                RuntimeTokenUsage? usage;
                try
                {
                    usage = JsonSerializer.Deserialize<RuntimeTokenUsage>(armadaEvent.Payload, jsonOptions);
                }
                catch (JsonException)
                {
                    continue;
                }

                if (usage == null || String.IsNullOrWhiteSpace(usage.Runtime) || String.IsNullOrWhiteSpace(usage.Model))
                    continue;

                TokenUsageRecord legacyRecord = new TokenUsageRecord
                {
                    TenantId = armadaEvent.TenantId,
                    UserId = armadaEvent.UserId,
                    Model = usage.Model,
                    Runtime = usage.Runtime,
                    Source = "mission",
                    SourceId = armadaEvent.MissionId ?? armadaEvent.EntityId,
                    VesselId = armadaEvent.VesselId,
                    CaptainId = armadaEvent.CaptainId,
                    InputTokens = usage.InputTokens,
                    OutputTokens = usage.OutputTokens,
                    CachedTokens = usage.CacheReadTokens,
                    TotalTokens = usage.ProviderTotalTokens ?? AddWithoutOverflow(usage.InputTokens, usage.OutputTokens),
                    Estimated = false,
                    CreatedUtc = armadaEvent.CreatedUtc
                };

                string key = BuildMatchKey(legacyRecord);
                if (availableTableRecords.TryGetValue(key, out int count) && count > 0)
                {
                    availableTableRecords[key] = count - 1;
                    continue;
                }

                merged.Add(legacyRecord);
            }

            return merged;
        }

        #endregion

        #region Private-Methods

        private static string BuildMatchKey(TokenUsageRecord record)
        {
            return String.Join(
                "\u001f",
                record.SourceId ?? String.Empty,
                record.Runtime ?? String.Empty,
                record.Model,
                record.InputTokens,
                record.OutputTokens,
                record.CachedTokens);
        }

        private static long AddWithoutOverflow(long left, long right)
        {
            if (right > 0 && left > long.MaxValue - right) return long.MaxValue;
            if (right < 0 && left < long.MinValue - right) return long.MinValue;
            return left + right;
        }

        #endregion
    }
}
