namespace Armada.Test.Database
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using SyslogLogging;

    /// <summary>
    /// Provider-backed data expiry: every expired table is purged by the retention rules on the live
    /// provider, and every row the rules keep survives. Runs last because a one-day retention purges
    /// any older row in the shared test database.
    /// </summary>
    internal sealed class DataExpiryDatabaseTests
    {
        private readonly DatabaseDriver _Driver;
        private readonly DatabaseSettings _Settings;
        private readonly bool _NoCleanup;

        internal DataExpiryDatabaseTests(DatabaseDriver driver, DatabaseSettings settings, bool noCleanup)
        {
            _Driver = driver ?? throw new ArgumentNullException(nameof(driver));
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _NoCleanup = noCleanup;
        }

        internal async Task VerifyRetentionPurgeAsync(CancellationToken token)
        {
            DatabaseFixture fixture = new DatabaseFixture(_Driver, _NoCleanup);
            try
            {
                DateTime now = DateTime.UtcNow;
                DateTime old = now.AddDays(-3);
                TenantMetadata tenant = await fixture.CreateTenantAsync("expiry", token: token).ConfigureAwait(false);
                UserMaster user = await fixture.CreateUserAsync(tenant.Id, "expiry", token: token).ConfigureAwait(false);
                Fleet fleet = await fixture.CreateFleetAsync(tenant.Id, user.Id, "expiry", token).ConfigureAwait(false);
                Vessel vessel = await fixture.CreateVesselAsync(tenant.Id, user.Id, fleet.Id, "expiry", token).ConfigureAwait(false);
                Captain captain = await fixture.CreateCaptainAsync(tenant.Id, user.Id, "expiry", token).ConfigureAwait(false);

                Voyage oldComplete = await fixture.CreateVoyageAsync(tenant.Id, user.Id, "expiry-old-complete", token, v => { v.Status = VoyageStatusEnum.Complete; v.CompletedUtc = old; }).ConfigureAwait(false);
                Voyage oldCancelled = await fixture.CreateVoyageAsync(tenant.Id, user.Id, "expiry-old-cancelled", token, v => { v.Status = VoyageStatusEnum.Cancelled; v.CompletedUtc = old; }).ConfigureAwait(false);
                Voyage oldFailed = await fixture.CreateVoyageAsync(tenant.Id, user.Id, "expiry-old-failed", token, v => { v.Status = VoyageStatusEnum.Failed; v.CompletedUtc = old; }).ConfigureAwait(false);
                Voyage recentComplete = await fixture.CreateVoyageAsync(tenant.Id, user.Id, "expiry-recent", token, v => { v.Status = VoyageStatusEnum.Complete; v.CompletedUtc = now; }).ConfigureAwait(false);

                Mission inOldVoyage = await fixture.CreateMissionAsync(tenant.Id, user.Id, oldComplete.Id, vessel.Id, captain.Id, "expiry-in-old-voyage", token, configure: m => m.Status = MissionStatusEnum.InProgress).ConfigureAwait(false);
                Mission inRecentVoyage = await fixture.CreateMissionAsync(tenant.Id, user.Id, recentComplete.Id, vessel.Id, captain.Id, "expiry-in-recent-voyage", token, completedUtc: old, configure: m => m.Status = MissionStatusEnum.Complete).ConfigureAwait(false);
                Mission standaloneOldFailed = await fixture.CreateMissionAsync(tenant.Id, user.Id, null!, vessel.Id, captain.Id, "expiry-standalone-old", token, completedUtc: old, configure: m => m.Status = MissionStatusEnum.Failed).ConfigureAwait(false);
                Mission childOfExpired = await fixture.CreateMissionAsync(tenant.Id, user.Id, null!, vessel.Id, captain.Id, "expiry-child", token, configure: m => { m.Status = MissionStatusEnum.Pending; m.ParentMissionId = standaloneOldFailed.Id; }).ConfigureAwait(false);
                Mission standaloneOldPending = await fixture.CreateMissionAsync(tenant.Id, user.Id, null!, vessel.Id, captain.Id, "expiry-standalone-pending", token, completedUtc: old, configure: m => m.Status = MissionStatusEnum.Pending).ConfigureAwait(false);
                Mission standaloneRecent = await fixture.CreateMissionAsync(tenant.Id, user.Id, null!, vessel.Id, captain.Id, "expiry-standalone-recent", token, completedUtc: now, configure: m => m.Status = MissionStatusEnum.Complete).ConfigureAwait(false);

                Signal oldRead = await CreateSignalAsync(tenant.Id, user.Id, true, old, token).ConfigureAwait(false);
                Signal oldUnread = await CreateSignalAsync(tenant.Id, user.Id, false, old, token).ConfigureAwait(false);
                Signal recentRead = await CreateSignalAsync(tenant.Id, user.Id, true, now, token).ConfigureAwait(false);

                ArmadaEvent oldEvent = await CreateEventAsync(tenant.Id, "mission.created", null, old, token).ConfigureAwait(false);
                ArmadaEvent recentEvent = await CreateEventAsync(tenant.Id, "mission.created", null, now, token).ConfigureAwait(false);
                ArmadaEvent attemptInsideLookBack = await CreateEventAsync(tenant.Id, ObjectiveDispatchAdmission.StartedEventType, ObjectiveDispatchAdmission.AttemptEntityType, old, token).ConfigureAwait(false);
                ArmadaEvent attemptBeyondLookBack = await CreateEventAsync(tenant.Id, ObjectiveDispatchAdmission.StartedEventType, ObjectiveDispatchAdmission.AttemptEntityType,
                    now - ObjectiveDispatchAdmission.ReconciliationLookBack - TimeSpan.FromDays(1), token).ConfigureAwait(false);

                Dock oldInactive = await fixture.CreateDockAsync(tenant.Id, user.Id, vessel.Id, null!, token, d => { d.Active = false; d.CaptainId = null; d.CreatedUtc = old; }).ConfigureAwait(false);
                Dock oldActive = await fixture.CreateDockAsync(tenant.Id, user.Id, vessel.Id, null!, token, d => { d.Active = true; d.CaptainId = null; d.CreatedUtc = old; }).ConfigureAwait(false);
                Dock oldInactiveHeld = await fixture.CreateDockAsync(tenant.Id, user.Id, vessel.Id, captain.Id, token, d => { d.Active = false; d.CreatedUtc = old; }).ConfigureAwait(false);
                Dock recentInactive = await fixture.CreateDockAsync(tenant.Id, user.Id, vessel.Id, null!, token, d => { d.Active = false; d.CaptainId = null; d.CreatedUtc = now; }).ConfigureAwait(false);

                MergeEntry oldLanded = await CreateMergeEntryAsync(tenant.Id, user.Id, vessel.Id, MergeStatusEnum.Landed, old, token).ConfigureAwait(false);
                MergeEntry oldFailedEntry = await CreateMergeEntryAsync(tenant.Id, user.Id, vessel.Id, MergeStatusEnum.Failed, old, token).ConfigureAwait(false);
                MergeEntry oldQueued = await CreateMergeEntryAsync(tenant.Id, user.Id, vessel.Id, MergeStatusEnum.Queued, old, token).ConfigureAwait(false);
                MergeEntry recentLanded = await CreateMergeEntryAsync(tenant.Id, user.Id, vessel.Id, MergeStatusEnum.Landed, now, token).ConfigureAwait(false);

                LoggingModule logging = new LoggingModule();
                logging.Settings.EnableConsole = false;
                DataExpiryService service = CreateService(logging, 1, 0);
                DataExpiryResult purged = await service.PurgeExpiredDataAsync(token).ConfigureAwait(false);
                DatabaseAssert.True(purged.Total >= 10, "Expiry deletes at least the seeded expired rows, deleted " + purged);
                foreach (string table in new[] { "missions", "voyages", "signals", "events", "docks", "merge_entries" })
                    DatabaseAssert.True(purged.Deleted(table) >= 1, "The summary counts rows deleted from " + table + ": " + purged);
                foreach (string table in DataExpiryCutoffs.ProductionFactTables)
                    DatabaseAssert.True(purged.Deleted(table) == null, "Fact retention 0 leaves " + table + " unpurged: " + purged);

                DatabaseAssert.True(await _Driver.Voyages.ReadAsync(oldComplete.Id, token).ConfigureAwait(false) == null, "An expired Complete voyage is purged");
                DatabaseAssert.True(await _Driver.Voyages.ReadAsync(oldCancelled.Id, token).ConfigureAwait(false) == null, "An expired Cancelled voyage is purged");
                DatabaseAssert.NotNull(await _Driver.Voyages.ReadAsync(oldFailed.Id, token).ConfigureAwait(false), "A Failed voyage is kept");
                DatabaseAssert.NotNull(await _Driver.Voyages.ReadAsync(recentComplete.Id, token).ConfigureAwait(false), "A recent voyage is kept");

                DatabaseAssert.True(await _Driver.Missions.ReadAsync(inOldVoyage.Id, token).ConfigureAwait(false) == null, "Every mission of an expired voyage is purged");
                DatabaseAssert.NotNull(await _Driver.Missions.ReadAsync(inRecentVoyage.Id, token).ConfigureAwait(false), "A mission of a retained voyage is kept");
                DatabaseAssert.True(await _Driver.Missions.ReadAsync(standaloneOldFailed.Id, token).ConfigureAwait(false) == null, "An expired terminal standalone mission is purged");
                Mission child = DatabaseAssert.NotNull(await _Driver.Missions.ReadAsync(childOfExpired.Id, token).ConfigureAwait(false), "A child of a purged mission is kept");
                DatabaseAssert.True(child.ParentMissionId == null, "A child of a purged mission loses its parent link");
                DatabaseAssert.NotNull(await _Driver.Missions.ReadAsync(standaloneOldPending.Id, token).ConfigureAwait(false), "A nonterminal standalone mission is kept");
                DatabaseAssert.NotNull(await _Driver.Missions.ReadAsync(standaloneRecent.Id, token).ConfigureAwait(false), "A recent standalone mission is kept");

                DatabaseAssert.True(await _Driver.Signals.ReadAsync(oldRead.Id, token).ConfigureAwait(false) == null, "An expired read signal is purged");
                DatabaseAssert.NotNull(await _Driver.Signals.ReadAsync(oldUnread.Id, token).ConfigureAwait(false), "An unread signal is kept");
                DatabaseAssert.NotNull(await _Driver.Signals.ReadAsync(recentRead.Id, token).ConfigureAwait(false), "A recent read signal is kept");

                DatabaseAssert.True(await _Driver.Events.ReadAsync(oldEvent.Id, token).ConfigureAwait(false) == null, "An expired event is purged");
                DatabaseAssert.NotNull(await _Driver.Events.ReadAsync(recentEvent.Id, token).ConfigureAwait(false), "A recent event is kept");
                DatabaseAssert.NotNull(await _Driver.Events.ReadAsync(attemptInsideLookBack.Id, token).ConfigureAwait(false), "A dispatch attempt record inside the reconciliation look-back is kept");
                DatabaseAssert.True(await _Driver.Events.ReadAsync(attemptBeyondLookBack.Id, token).ConfigureAwait(false) == null, "A dispatch attempt record beyond the look-back follows retention");

                DatabaseAssert.True(await _Driver.Docks.ReadAsync(oldInactive.Id, token).ConfigureAwait(false) == null, "An expired inactive unheld dock is purged");
                DatabaseAssert.NotNull(await _Driver.Docks.ReadAsync(oldActive.Id, token).ConfigureAwait(false), "An active dock is kept");
                DatabaseAssert.NotNull(await _Driver.Docks.ReadAsync(oldInactiveHeld.Id, token).ConfigureAwait(false), "A dock with a captain is kept");
                DatabaseAssert.NotNull(await _Driver.Docks.ReadAsync(recentInactive.Id, token).ConfigureAwait(false), "A recent inactive dock is kept");

                DatabaseAssert.True(await _Driver.MergeEntries.ReadAsync(oldLanded.Id, token).ConfigureAwait(false) == null, "An expired Landed merge entry is purged");
                DatabaseAssert.True(await _Driver.MergeEntries.ReadAsync(oldFailedEntry.Id, token).ConfigureAwait(false) == null, "An expired Failed merge entry is purged");
                DatabaseAssert.NotNull(await _Driver.MergeEntries.ReadAsync(oldQueued.Id, token).ConfigureAwait(false), "A queued merge entry is kept");
                DatabaseAssert.NotNull(await _Driver.MergeEntries.ReadAsync(recentLanded.Id, token).ConfigureAwait(false), "A recent Landed merge entry is kept");
            }
            finally
            {
                await fixture.CleanupAsync(token).ConfigureAwait(false);
            }
        }

        internal async Task VerifyDurableEventsSurviveRetentionAsync(CancellationToken token)
        {
            DateTime now = DateTime.UtcNow;
            string suffix = Guid.NewGuid().ToString("N").Substring(0, 12);
            List<string> created = new List<string>();
            try
            {
                string openIncident = "inc_open_" + suffix;
                string revisedIncident = "inc_revised_" + suffix;
                string recentIncident = "inc_recent_" + suffix;

                ArmadaEvent openOnly = await CreateDurableEventAsync(created, IncidentService.SnapshotEventType, IncidentService.IncidentEntityType, openIncident, now.AddDays(-30), token).ConfigureAwait(false);
                ArmadaEvent revisedOlder = await CreateDurableEventAsync(created, IncidentService.SnapshotEventType, IncidentService.IncidentEntityType, revisedIncident, now.AddDays(-20), token).ConfigureAwait(false);
                ArmadaEvent revisedNewest = await CreateDurableEventAsync(created, IncidentService.SnapshotEventType, IncidentService.IncidentEntityType, revisedIncident, now.AddDays(-10), token).ConfigureAwait(false);
                ArmadaEvent oldBeforeRecent = await CreateDurableEventAsync(created, IncidentService.SnapshotEventType, IncidentService.IncidentEntityType, recentIncident, now.AddDays(-10), token).ConfigureAwait(false);
                ArmadaEvent recentSnapshot = await CreateDurableEventAsync(created, IncidentService.SnapshotEventType, IncidentService.IncidentEntityType, recentIncident, now, token).ConfigureAwait(false);
                ArmadaEvent tombstone = await CreateDurableEventAsync(created, ObjectiveService.DeletedEventType, "objective", "obj_deleted_" + suffix, now.AddDays(-30), token).ConfigureAwait(false);
                ArmadaEvent reversal = await CreateDurableEventAsync(created, TypedDecisionRecorder.EventTypeReversed, "mission", "msn_reversed_" + suffix, now.AddDays(-30), token).ConfigureAwait(false);
                ArmadaEvent ordinary = await CreateDurableEventAsync(created, "mission.created", "mission", "msn_ordinary_" + suffix, now.AddDays(-30), token).ConfigureAwait(false);

                LoggingModule logging = new LoggingModule();
                logging.Settings.EnableConsole = false;
                DataExpiryResult purged = await CreateService(logging, 1, 0).PurgeExpiredDataAsync(token).ConfigureAwait(false);

                DatabaseAssert.NotNull(await _Driver.Events.ReadAsync(openOnly.Id, token).ConfigureAwait(false), "An incident whose only snapshot is older than the cutoff keeps it");
                DatabaseAssert.True(await _Driver.Events.ReadAsync(revisedOlder.Id, token).ConfigureAwait(false) == null, "An older snapshot of an incident with a newer one expires");
                DatabaseAssert.NotNull(await _Driver.Events.ReadAsync(revisedNewest.Id, token).ConfigureAwait(false), "The newest snapshot of an incident is kept whatever its age");
                DatabaseAssert.True(await _Driver.Events.ReadAsync(oldBeforeRecent.Id, token).ConfigureAwait(false) == null, "An old snapshot superseded by a recent one expires");
                DatabaseAssert.NotNull(await _Driver.Events.ReadAsync(recentSnapshot.Id, token).ConfigureAwait(false), "A recent snapshot is kept");
                DatabaseAssert.NotNull(await _Driver.Events.ReadAsync(tombstone.Id, token).ConfigureAwait(false), "An objective deletion tombstone is kept whatever its age");
                DatabaseAssert.NotNull(await _Driver.Events.ReadAsync(reversal.Id, token).ConfigureAwait(false), "A typed-decision reversal is kept whatever its age");
                DatabaseAssert.True(await _Driver.Events.ReadAsync(ordinary.Id, token).ConfigureAwait(false) == null, "An ordinary old event still expires");
                DatabaseAssert.True(purged.Kept("incident_latest") >= 2, "The summary counts kept incident snapshots: " + purged);
                DatabaseAssert.True(purged.Kept("tombstones") >= 1, "The summary counts kept tombstones: " + purged);
                DatabaseAssert.True(purged.Kept("reversals") >= 1, "The summary counts kept reversals: " + purged);
                DatabaseAssert.True(purged.ToString().Contains("kept_incident_latest=", StringComparison.Ordinal), "The summary names the kept classes: " + purged);
            }
            finally
            {
                if (!_NoCleanup)
                {
                    foreach (string id in created)
                        await _Driver.Events.DeleteAsync(id, token).ConfigureAwait(false);
                }
            }
        }

        internal async Task VerifyProductionFactRetentionAsync(CancellationToken token)
        {
            string suffix = Guid.NewGuid().ToString("N").Substring(0, 12);
            DateTime now = DateTime.UtcNow;
            DateTime expired = now.AddDays(-400);
            DateTime retained = now.AddDays(-300);

            MissionAttemptFact oldFact = await _Driver.MissionAttemptFacts.CreateAsync(NewFact("msn_old_" + suffix, expired), token).ConfigureAwait(false);
            MissionAttemptFact newFact = await _Driver.MissionAttemptFacts.CreateAsync(NewFact("msn_new_" + suffix, retained), token).ConfigureAwait(false);
            PreparationClaimObservation oldObservation = await _Driver.PreparationClaimObservations.CreateAsync(NewObservation("opc_old_" + suffix, expired), token).ConfigureAwait(false);
            PreparationClaimObservation newObservation = await _Driver.PreparationClaimObservations.CreateAsync(NewObservation("opc_new_" + suffix, retained), token).ConfigureAwait(false);
            LaneStateTransition oldLane = await _Driver.LaneStateTransitions.CreateAsync(NewLane("lane_old_" + suffix, expired), token).ConfigureAwait(false);
            LaneStateTransition newLane = await _Driver.LaneStateTransitions.CreateAsync(NewLane("lane_new_" + suffix, retained), token).ConfigureAwait(false);

            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            DataExpiryResult purged = await CreateService(logging, 0, 365).PurgeExpiredDataAsync(token).ConfigureAwait(false);

            foreach (string table in DataExpiryCutoffs.ProductionFactTables)
                DatabaseAssert.True(purged.Deleted(table) >= 1, "The summary counts rows deleted from " + table + ": " + purged);
            DatabaseAssert.True(purged.Deleted("events") == null, "Data retention 0 leaves operational tables unpurged: " + purged);

            DatabaseAssert.True(!await FactExistsAsync(oldFact.MissionId, expired, token).ConfigureAwait(false), "A mission attempt fact older than the fact retention is purged");
            DatabaseAssert.True(await FactExistsAsync(newFact.MissionId, retained, token).ConfigureAwait(false), "A mission attempt fact inside the fact retention is kept");
            DatabaseAssert.True(!await ObservationExistsAsync(oldObservation.ClaimId, expired, token).ConfigureAwait(false), "A claim observation older than the fact retention is purged");
            DatabaseAssert.True(await ObservationExistsAsync(newObservation.ClaimId, retained, token).ConfigureAwait(false), "A claim observation inside the fact retention is kept");
            DatabaseAssert.True(!await LaneExistsAsync(oldLane.LaneKey, expired, token).ConfigureAwait(false), "A lane state transition older than the fact retention is purged");
            DatabaseAssert.True(await LaneExistsAsync(newLane.LaneKey, retained, token).ConfigureAwait(false), "A lane state transition inside the fact retention is kept");
        }

        internal async Task VerifyRequestHistoryRetentionAsync(CancellationToken token)
        {
            DateTime now = DateTime.UtcNow;
            string route = "/api/v1/retention-probe/" + Guid.NewGuid().ToString("N");
            RequestHistoryEntry expired = new RequestHistoryEntry { Route = route, CreatedUtc = now.AddDays(-3) };
            RequestHistoryEntry justInside = new RequestHistoryEntry { Route = route, CreatedUtc = now.AddDays(-2).AddMinutes(1) };
            RequestHistoryEntry recent = new RequestHistoryEntry { Route = route, CreatedUtc = now };
            await _Driver.RequestHistory.CreateAsync(expired, new RequestHistoryDetail { RequestHistoryId = expired.Id, RequestBodyText = "expired" }, token).ConfigureAwait(false);
            await _Driver.RequestHistory.CreateAsync(justInside, new RequestHistoryDetail { RequestHistoryId = justInside.Id, RequestBodyText = "inside" }, token).ConfigureAwait(false);
            await _Driver.RequestHistory.CreateAsync(recent, null, token).ConfigureAwait(false);
            try
            {
                LoggingModule logging = new LoggingModule();
                logging.Settings.EnableConsole = false;
                DataExpiryResult purged = await new DataExpiryService(logging, _Driver, 0, 0, 2).PurgeExpiredDataAsync(token).ConfigureAwait(false);

                DatabaseAssert.True(purged.Deleted("request_history") >= 1, "The summary counts request history deleted: " + purged);
                DatabaseAssert.True(purged.Deleted("request_history_detail") >= 1, "The summary counts request detail deleted: " + purged);
                DatabaseAssert.True(purged.Deleted("events") == null, "Data retention 0 leaves operational tables unpurged: " + purged);
                DatabaseAssert.True(await _Driver.RequestHistory.ReadAsync(expired.Id, null, token).ConfigureAwait(false) == null, "A request older than the request-history retention is purged");
                RequestHistoryRecord inside = DatabaseAssert.NotNull(await _Driver.RequestHistory.ReadAsync(justInside.Id, null, token).ConfigureAwait(false), "A request just inside the retention period is kept");
                DatabaseAssert.Equal("inside", inside.Detail?.RequestBodyText, "A kept request keeps its detail");
                DatabaseAssert.NotNull(await _Driver.RequestHistory.ReadAsync(recent.Id, null, token).ConfigureAwait(false), "A recent request is kept");
            }
            finally
            {
                if (!_NoCleanup)
                {
                    foreach (string id in new[] { expired.Id, justInside.Id, recent.Id })
                        await _Driver.RequestHistory.DeleteAsync(id, null, token).ConfigureAwait(false);
                }
            }
        }

        private static ProductionFactQuery Around(DateTime createdUtc)
        {
            return new ProductionFactQuery { FromUtc = createdUtc.AddMinutes(-1), ToUtc = createdUtc.AddMinutes(1), Limit = 1000 };
        }

        private async Task<bool> FactExistsAsync(string missionId, DateTime createdUtc, CancellationToken token)
        {
            ProductionFactPage<MissionAttemptFact> page = await _Driver.MissionAttemptFacts.EnumerateAsync(Around(createdUtc), token).ConfigureAwait(false);
            return page.Items.Exists(item => item.MissionId == missionId);
        }

        private async Task<bool> ObservationExistsAsync(string claimId, DateTime createdUtc, CancellationToken token)
        {
            ProductionFactPage<PreparationClaimObservation> page = await _Driver.PreparationClaimObservations.EnumerateAsync(Around(createdUtc), token).ConfigureAwait(false);
            return page.Items.Exists(item => item.ClaimId == claimId);
        }

        private async Task<bool> LaneExistsAsync(string laneKey, DateTime createdUtc, CancellationToken token)
        {
            ProductionFactPage<LaneStateTransition> page = await _Driver.LaneStateTransitions.EnumerateAsync(Around(createdUtc), token).ConfigureAwait(false);
            return page.Items.Exists(item => item.LaneKey == laneKey);
        }

        private static MissionAttemptFact NewFact(string missionId, DateTime createdUtc)
        {
            return new MissionAttemptFact { MissionId = missionId, RootMissionId = missionId, FactType = MissionAttemptFactTypeEnum.AttemptStarted, CreatedUtc = createdUtc };
        }

        private static PreparationClaimObservation NewObservation(string claimId, DateTime createdUtc)
        {
            return new PreparationClaimObservation { ObjectiveId = "obj_expiry", ClaimId = claimId, EvidenceFingerprint = new string('a', 64), CreatedUtc = createdUtc };
        }

        private static LaneStateTransition NewLane(string laneKey, DateTime createdUtc)
        {
            return new LaneStateTransition { LaneKey = laneKey, EligibleCount = 1, Occupied = 0, Capacity = 1, ValidForSeconds = 120, CreatedUtc = createdUtc };
        }

        private DataExpiryService CreateService(LoggingModule logging, int retentionDays, int productionFactRetentionDays)
        {
            return new DataExpiryService(logging, _Driver, retentionDays, productionFactRetentionDays);
        }

        private async Task<Signal> CreateSignalAsync(string tenantId, string userId, bool read, DateTime createdUtc, CancellationToken token)
        {
            Signal signal = new Signal(SignalTypeEnum.Nudge, "expiry") { TenantId = tenantId, UserId = userId, Read = read, CreatedUtc = createdUtc };
            return await _Driver.Signals.CreateAsync(signal, token).ConfigureAwait(false);
        }

        private async Task<ArmadaEvent> CreateEventAsync(string tenantId, string eventType, string? entityType, DateTime createdUtc, CancellationToken token)
        {
            ArmadaEvent evt = new ArmadaEvent(eventType, "expiry")
            {
                TenantId = tenantId,
                EntityType = entityType,
                EntityId = entityType == null ? null : "attempt-" + Guid.NewGuid().ToString("N"),
                CreatedUtc = createdUtc
            };
            return await _Driver.Events.CreateAsync(evt, token).ConfigureAwait(false);
        }

        private async Task<ArmadaEvent> CreateDurableEventAsync(List<string> created, string eventType, string entityType, string entityId, DateTime createdUtc, CancellationToken token)
        {
            ArmadaEvent evt = new ArmadaEvent(eventType, "expiry")
            {
                TenantId = null,
                EntityType = entityType,
                EntityId = entityId,
                CreatedUtc = createdUtc
            };
            ArmadaEvent stored = await _Driver.Events.CreateAsync(evt, token).ConfigureAwait(false);
            created.Add(stored.Id);
            return stored;
        }

        private async Task<MergeEntry> CreateMergeEntryAsync(string tenantId, string userId, string vesselId, MergeStatusEnum status, DateTime completedUtc, CancellationToken token)
        {
            MergeEntry entry = new MergeEntry("expiry/" + Guid.NewGuid().ToString("N"))
            {
                TenantId = tenantId,
                UserId = userId,
                VesselId = vesselId,
                Status = status,
                CreatedUtc = completedUtc,
                CompletedUtc = completedUtc
            };
            return await _Driver.MergeEntries.CreateAsync(entry, token).ConfigureAwait(false);
        }
    }
}
