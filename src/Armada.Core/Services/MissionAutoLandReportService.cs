namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using SyslogLogging;

    /// <summary>
    /// Builds the read-only auto-land report for a mission from the vessel's predicate, recorded auto-land events and
    /// the mission's merge entry. It never evaluates a predicate, reads a diff, or infers a decision from status.
    /// </summary>
    public class MissionAutoLandReportService
    {
        #region Public-Members

        /// <summary>
        /// Event type recorded when the predicate passes.
        /// </summary>
        public const string TriggeredEventType = "merge_queue.auto_land_triggered";

        /// <summary>
        /// Event type recorded when the predicate fails or the diff is unavailable.
        /// </summary>
        public const string SkippedEventType = "merge_queue.auto_land_skipped";

        /// <summary>
        /// Maximum reported length of a skip reason.
        /// </summary>
        public const int MaxReasonLength = 2000;

        #endregion

        #region Private-Members

        private readonly DatabaseDriver _Database;
        private readonly LoggingModule _Logging;
        private const string _Header = "[MissionAutoLandReportService] ";

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="logging">Logging module.</param>
        public MissionAutoLandReportService(DatabaseDriver database, LoggingModule logging)
        {
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Build the report for a mission the caller is already authorized to read.
        /// </summary>
        /// <param name="auth">Caller scope; related records are read in the same scope.</param>
        /// <param name="mission">Mission read in the caller's scope.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Auto-land report.</returns>
        public async Task<MissionAutoLandReport> GetForMissionAsync(AuthContext auth, Mission mission, CancellationToken token = default)
        {
            if (auth == null) throw new ArgumentNullException(nameof(auth));
            if (mission == null) throw new ArgumentNullException(nameof(mission));

            MissionAutoLandReport report = new MissionAutoLandReport { MissionId = mission.Id };
            await PopulatePredicateAsync(auth, mission, report, token).ConfigureAwait(false);
            await PopulateDecisionAsync(auth, mission, report, token).ConfigureAwait(false);
            await PopulateMergeEntryAsync(auth, mission, report, token).ConfigureAwait(false);

            // Events carry the mission owner's scope, but a merge entry carries a tenant only when the mission and
            // vessel share it, so a scoped reader can see a decision whose entry it cannot read. Say so explicitly.
            if (report.LatestDecision != null
                && report.LatestMergeEntry == null
                && report.MergeEntryUnavailableReason == null)
            {
                report.MergeEntryUnavailableReason = "the decided merge entry is not visible in the current scope";
            }

            return report;
        }

        #endregion

        #region Private-Methods

        private async Task PopulatePredicateAsync(AuthContext auth, Mission mission, MissionAutoLandReport report, CancellationToken token)
        {
            if (String.IsNullOrWhiteSpace(mission.VesselId))
            {
                report.PredicateUnavailableReason = "the mission has no vessel";
                return;
            }

            try
            {
                Vessel? vessel = auth.IsAdmin
                    ? await _Database.Vessels.ReadAsync(mission.VesselId, token).ConfigureAwait(false)
                    : auth.IsTenantAdmin
                        ? await _Database.Vessels.ReadAsync(auth.TenantId!, mission.VesselId, token).ConfigureAwait(false)
                        : await _Database.Vessels.ReadAsync(auth.TenantId!, auth.UserId!, mission.VesselId, token).ConfigureAwait(false);
                if (vessel == null)
                {
                    report.PredicateUnavailableReason = "the mission vessel cannot be read in the current scope";
                    return;
                }

                report.PredicateConfigured = !String.IsNullOrWhiteSpace(vessel.AutoLandPredicate);
                report.CurrentPredicate = vessel.GetAutoLandPredicate();
                if (report.PredicateConfigured && report.CurrentPredicate == null)
                    report.PredicateUnavailableReason = "the configured predicate cannot be parsed";
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                report.CurrentPredicate = null;
                report.PredicateUnavailableReason = "the vessel could not be read (" + ex.GetType().Name + ")";
                _Logging.Warn(_Header + "could not read vessel for mission " + mission.Id + ": " + ex.Message);
            }
        }

        private async Task PopulateDecisionAsync(AuthContext auth, Mission mission, MissionAutoLandReport report, CancellationToken token)
        {
            List<ArmadaEvent> latest = new List<ArmadaEvent>();
            try
            {
                foreach (string eventType in new string[] { TriggeredEventType, SkippedEventType })
                {
                    EnumerationQuery query = new EnumerationQuery
                    {
                        MissionId = mission.Id,
                        EventType = eventType,
                        Order = EnumerationOrderEnum.CreatedDescending,
                        PageNumber = 1,
                        PageSize = 2
                    };
                    EnumerationResult<ArmadaEvent> result = auth.IsAdmin
                        ? await _Database.Events.EnumerateAsync(query, token).ConfigureAwait(false)
                        : auth.IsTenantAdmin
                            ? await _Database.Events.EnumerateAsync(auth.TenantId!, query, token).ConfigureAwait(false)
                            : await _Database.Events.EnumerateAsync(auth.TenantId!, auth.UserId!, query, token).ConfigureAwait(false);
                    if (result.Objects != null) latest.AddRange(result.Objects);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                SetDecisionUnavailable(report, "auto-land events could not be read (" + ex.GetType().Name + ")");
                _Logging.Warn(_Header + "could not read auto-land events for mission " + mission.Id + ": " + ex.Message);
                return;
            }

            List<ArmadaEvent> ordered = latest.OrderByDescending(item => item.CreatedUtc).ToList();
            if (ordered.Count == 0)
            {
                report.DecisionState = RecordedHistoryStateEnum.NotRecorded;
                return;
            }

            if (ordered.Count > 1 && ordered[0].CreatedUtc == ordered[1].CreatedUtc)
            {
                SetDecisionUnavailable(report, "the latest auto-land events share a timestamp, so their order cannot be determined");
                return;
            }

            ArmadaEvent newest = ordered[0];
            StoredAutoLandPayload? payload = null;
            if (!String.IsNullOrWhiteSpace(newest.Payload))
            {
                try
                {
                    payload = JsonSerializer.Deserialize<StoredAutoLandPayload>(newest.Payload);
                }
                catch (JsonException)
                {
                    payload = null;
                }
            }

            if (payload == null || String.IsNullOrWhiteSpace(payload.EntryId))
            {
                SetDecisionUnavailable(report, "the latest auto-land event has a missing or malformed payload");
                return;
            }

            bool skipped = String.Equals(newest.EventType, SkippedEventType, StringComparison.Ordinal);
            report.DecisionState = RecordedHistoryStateEnum.Recorded;
            report.LatestDecision = new MissionAutoLandDecision
            {
                Outcome = skipped ? AutoLandDecisionOutcomeEnum.Skipped : AutoLandDecisionOutcomeEnum.Triggered,
                EventId = newest.Id,
                MergeEntryId = payload.EntryId,
                Reason = skipped ? Bound(payload.Reason) : null,
                PredicateAtDecision = payload.Predicate,
                CreatedUtc = newest.CreatedUtc
            };
        }

        private async Task PopulateMergeEntryAsync(AuthContext auth, Mission mission, MissionAutoLandReport report, CancellationToken token)
        {
            EnumerationQuery query = new EnumerationQuery
            {
                MissionId = mission.Id,
                Order = EnumerationOrderEnum.CreatedDescending,
                PageNumber = 1,
                PageSize = 2
            };

            try
            {
                EnumerationResult<MergeEntry> result = auth.IsAdmin
                    ? await _Database.MergeEntries.EnumerateAsync(query, token).ConfigureAwait(false)
                    : auth.IsTenantAdmin
                        ? await _Database.MergeEntries.EnumerateAsync(auth.TenantId!, query, token).ConfigureAwait(false)
                        : await _Database.MergeEntries.EnumerateAsync(auth.TenantId!, auth.UserId!, query, token).ConfigureAwait(false);
                List<MergeEntry> entries = result.Objects ?? new List<MergeEntry>();
                if (entries.Count == 0) return;
                if (entries.Count > 1 && entries[0].CreatedUtc == entries[1].CreatedUtc)
                {
                    report.MergeEntryUnavailableReason = "the latest merge entries share a creation time, so their order cannot be determined";
                    return;
                }

                MergeEntry entry = entries[0];

                report.LatestMergeEntry = new MissionAutoLandMergeEntry
                {
                    EntryId = entry.Id,
                    Status = entry.Status,
                    AuditLane = entry.AuditLane,
                    AuditConventionPassed = entry.AuditConventionPassed,
                    AuditCriticalTrigger = entry.AuditCriticalTrigger,
                    AuditDeepPicked = entry.AuditDeepPicked,
                    AuditDeepVerdict = entry.AuditDeepVerdict,
                    AuditDeepCompletedUtc = entry.AuditDeepCompletedUtc,
                    CreatedUtc = entry.CreatedUtc,
                    LastUpdateUtc = entry.LastUpdateUtc
                };
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                report.LatestMergeEntry = null;
                report.MergeEntryUnavailableReason = "the merge entry could not be read (" + ex.GetType().Name + ")";
                _Logging.Warn(_Header + "could not read merge entry for mission " + mission.Id + ": " + ex.Message);
            }
        }

        private static void SetDecisionUnavailable(MissionAutoLandReport report, string reason)
        {
            report.DecisionState = RecordedHistoryStateEnum.Unavailable;
            report.DecisionUnavailableReason = reason;
            report.LatestDecision = null;
        }

        private static string? Bound(string? text)
        {
            if (text == null) return null;
            string redacted = SecretRedactor.Redact(text);
            if (redacted.Length <= MaxReasonLength) return redacted;
            const string marker = "\n...(truncated)";
            return redacted.Substring(0, MaxReasonLength - marker.Length) + marker;
        }

        #endregion

        #region Private-Classes

        /// <summary>
        /// Payload written with auto-land events by the landing handler and the landing-drain safety net.
        /// </summary>
        private sealed class StoredAutoLandPayload
        {
            [JsonPropertyName("entryId")]
            public string? EntryId { get; set; }

            [JsonPropertyName("reason")]
            public string? Reason { get; set; }

            [JsonPropertyName("predicate")]
            public AutoLandPredicate? Predicate { get; set; }
        }

        #endregion
    }
}
