namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using Armada.Core.Enums;
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
            return MergeLegacyEvents(records, events, records, jsonOptions);
        }

        /// <summary>
        /// Add legacy mission token-usage events that have no matching table record anywhere in time.
        /// <para>
        /// The current capture path writes a table record and then its event, so the two carry slightly
        /// different creation times. Near a window edge one of them can fall inside the window and the
        /// other outside. Matching an event only against the in-window records would then count the usage
        /// as a legacy event in one window and as a table record in the adjacent one. The match is therefore
        /// made against <paramref name="identityRecords"/>: every table record for the missions the events
        /// name, whatever their creation time. The usage is counted where its table record lies.
        /// </para>
        /// </summary>
        /// <param name="records">Table records inside the summary window; these are counted.</param>
        /// <param name="events">Legacy mission.token_usage events inside the summary window.</param>
        /// <param name="identityRecords">Every table record for the missions the events name, in any window.</param>
        /// <param name="jsonOptions">JSON options used for event payloads.</param>
        /// <returns>The in-window records plus the events that have no table record.</returns>
        public static List<TokenUsageRecord> MergeLegacyEvents(
            List<TokenUsageRecord> records,
            List<ArmadaEvent> events,
            List<TokenUsageRecord> identityRecords,
            JsonSerializerOptions jsonOptions)
        {
            if (records == null) throw new ArgumentNullException(nameof(records));
            if (events == null) throw new ArgumentNullException(nameof(events));
            if (identityRecords == null) throw new ArgumentNullException(nameof(identityRecords));
            if (jsonOptions == null) throw new ArgumentNullException(nameof(jsonOptions));

            List<TokenUsageRecord> merged = new List<TokenUsageRecord>(records);
            Dictionary<string, int> availableTableRecords = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (TokenUsageRecord record in identityRecords)
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
                    UsageRule = usage.UsageRule,
                    InputTokens = usage.InputTokens,
                    OutputTokens = usage.OutputTokens,
                    CachedTokens = usage.CacheReadTokens,
                    TotalTokens = usage.ProviderTotalTokens ?? AddWithoutOverflow(usage.InputTokens, usage.OutputTokens),
                    Estimated = false,
                    CreatedUtc = armadaEvent.CreatedUtc
                };
                if (usage.UsageRule == TokenUsageRuleEnum.SeparateInputBuckets)
                {
                    legacyRecord.UncachedInputTokens = usage.UncachedInputTokens;
                    legacyRecord.CacheReadInputTokens = usage.CacheReadTokens;
                    legacyRecord.CacheWriteInputTokens = usage.CacheWriteTokens;
                }

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
