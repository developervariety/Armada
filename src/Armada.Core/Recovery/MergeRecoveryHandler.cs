namespace Armada.Core.Recovery
{
    using System;
    using System.Collections.Concurrent;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using SyslogLogging;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;

    /// <summary>
    /// Admiral-side handler that fires when a merge-queue entry transitions to Failed.
    /// Reads the persisted classification, consults <see cref="IRecoveryRouter"/>, and
    /// executes the chosen terminal action (redispatch, rebase-captain, or surface).
    /// Increments <see cref="Mission.RecoveryAttempts"/> with mission-scoped row locking
    /// so concurrent fail-events for the same mission cannot double-increment.
    /// </summary>
    public sealed class MergeRecoveryHandler : IMergeRecoveryHandler
    {
        #region Private-Members

        private const string _Header = "[MergeRecoveryHandler] ";
        private const string _RecoveryDispatchFailedReason = "recovery_dispatch_failed";
        private const string _ConflictTrivialFileCap = "2";
        private const string _ConflictTrivialDiffCap = "100";

        // Surface only after this many *consecutive* admiral.RestartMissionAsync exceptions
        // for the same mission. Transient admiral hiccups should not permanently downgrade
        // a recoverable entry to PR-fallback; a real outage will exceed the cap and surface.
        private const int _AdmiralDispatchFailureThreshold = 3;

        private readonly LoggingModule _Logging;
        private readonly DatabaseDriver _Database;
        private readonly IRecoveryRouter _Router;
        private readonly IAdmiralService _Admiral;
        private readonly ArmadaSettings _Settings;
        private readonly IRebaseCaptainDockSetup? _RebaseCaptainDockSetup;

        /// <summary>
        /// Per-mission semaphores ensuring at most one in-flight RecoveryAttempts increment
        /// for a given mission, even when several merge-entry failures arrive concurrently.
        /// </summary>
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _MissionLocks =
            new ConcurrentDictionary<string, SemaphoreSlim>();

        /// <summary>
        /// Per-mission counter of *consecutive* admiral.RestartMissionAsync exceptions.
        /// Cleared on a successful redispatch. When the count reaches
        /// <see cref="_AdmiralDispatchFailureThreshold"/> the entry is surfaced with
        /// reason <see cref="_RecoveryDispatchFailedReason"/> and the counter is reset.
        /// In-memory only; a process restart resets the counter, which is acceptable
        /// because a recurring admiral outage will cross the threshold again quickly.
        /// </summary>
        private readonly ConcurrentDictionary<string, int> _AdmiralDispatchFailureCounts =
            new ConcurrentDictionary<string, int>();

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="logging">Logging module (required).</param>
        /// <param name="database">Database driver (required).</param>
        /// <param name="router">Recovery router (required).</param>
        /// <param name="admiral">Admiral service used to redispatch missions (required).</param>
        /// <param name="settings">Application settings (required).</param>
        /// <param name="rebaseCaptainDockSetup">Optional rebase-captain dock setup (M4 wiring; not invoked in M3).</param>
        public MergeRecoveryHandler(
            LoggingModule logging,
            DatabaseDriver database,
            IRecoveryRouter router,
            IAdmiralService admiral,
            ArmadaSettings settings,
            IRebaseCaptainDockSetup? rebaseCaptainDockSetup = null)
        {
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _Router = router ?? throw new ArgumentNullException(nameof(router));
            _Admiral = admiral ?? throw new ArgumentNullException(nameof(admiral));
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _RebaseCaptainDockSetup = rebaseCaptainDockSetup;
        }

        #endregion

        #region Public-Methods

        /// <inheritdoc />
        public async Task OnMergeFailedAsync(string mergeEntryId, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(mergeEntryId)) throw new ArgumentNullException(nameof(mergeEntryId));

            MergeEntry? entry = await _Database.MergeEntries.ReadAsync(mergeEntryId, token).ConfigureAwait(false);
            if (entry == null)
            {
                _Logging.Warn(_Header + "merge entry " + mergeEntryId + " not found; skipping recovery");
                return;
            }

            if (!String.IsNullOrEmpty(entry.AuditCriticalTrigger))
            {
                _Logging.Info(_Header + "merge entry " + mergeEntryId + " already has audit_critical_trigger '" + entry.AuditCriticalTrigger + "'; PR-fallback won, skipping recovery");
                return;
            }

            if (String.IsNullOrEmpty(entry.MissionId))
            {
                _Logging.Warn(_Header + "merge entry " + mergeEntryId + " has no MissionId; cannot route recovery");
                return;
            }

            Mission? mission = await _Database.Missions.ReadAsync(entry.MissionId, token).ConfigureAwait(false);
            if (mission == null)
            {
                _Logging.Warn(_Header + "mission " + entry.MissionId + " not found for merge entry " + mergeEntryId + "; cannot route recovery");
                return;
            }

            int conflictedCount = ParseConflictedFileCount(entry.ConflictedFiles);
            bool conflictTrivial = (conflictedCount <= 2 && entry.DiffLineCount <= 100);
            MergeFailureClassEnum failureClass = entry.MergeFailureClass ?? MergeFailureClassEnum.Unknown;

            SemaphoreSlim missionLock = _MissionLocks.GetOrAdd(mission.Id, _ => new SemaphoreSlim(1, 1));
            await missionLock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                // Re-read mission inside the lock so RecoveryAttempts reflects any prior increment.
                Mission? freshMission = await _Database.Missions.ReadAsync(mission.Id, token).ConfigureAwait(false);
                if (freshMission != null) mission = freshMission;

                RecoveryAction action = _Router.Route(failureClass, conflictTrivial, mission.RecoveryAttempts);
                _Logging.Info(_Header + "routing merge entry " + mergeEntryId + " (class=" + failureClass + ", trivial=" + conflictTrivial + ", attempts=" + mission.RecoveryAttempts + ") -> " + action.GetType().Name);

                switch (action)
                {
                    case RecoveryAction.Redispatch:
                        await ExecuteRedispatchAsync(entry, mission, failureClass, token).ConfigureAwait(false);
                        break;

                    case RecoveryAction.Surface surface:
                        await ExecuteSurfaceAsync(entry, surface.Reason, mission, failureClass, token).ConfigureAwait(false);
                        break;

                    case RecoveryAction.RebaseCaptain:
                        throw new NotImplementedException("RebaseCaptain branch lands in M4");

                    default:
                        _Logging.Warn(_Header + "router returned unknown action " + action.GetType().Name + " for merge entry " + mergeEntryId);
                        break;
                }
            }
            finally
            {
                missionLock.Release();
            }
        }

        #endregion

        #region Private-Methods

        private async Task ExecuteRedispatchAsync(MergeEntry entry, Mission mission, MergeFailureClassEnum failureClass, CancellationToken token)
        {
            int previousAttempts = mission.RecoveryAttempts;
            DateTime now = DateTime.UtcNow;

            // Increment + persist BEFORE calling the admiral so a successful dispatch sees the
            // new counter. Roll back if the admiral throws so we do not consume a recovery slot
            // on a failure to communicate.
            mission.RecoveryAttempts = previousAttempts + 1;
            mission.LastRecoveryActionUtc = now;
            mission.LastUpdateUtc = now;
            await _Database.Missions.UpdateAsync(mission, token).ConfigureAwait(false);

            try
            {
                await _Admiral.RestartMissionAsync(mission.Id, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Revert the increment so a future attempt can still recover.
                _Logging.Error(_Header + "admiral.RestartMissionAsync failed for mission " + mission.Id + ": " + ex.Message);
                try
                {
                    Mission? rollback = await _Database.Missions.ReadAsync(mission.Id, token).ConfigureAwait(false);
                    if (rollback != null)
                    {
                        rollback.RecoveryAttempts = previousAttempts;
                        rollback.LastRecoveryActionUtc = mission.LastRecoveryActionUtc;
                        rollback.LastUpdateUtc = DateTime.UtcNow;
                        await _Database.Missions.UpdateAsync(rollback, token).ConfigureAwait(false);
                    }
                }
                catch (Exception rollbackEx)
                {
                    _Logging.Warn(_Header + "rollback of RecoveryAttempts failed for mission " + mission.Id + ": " + rollbackEx.Message);
                }

                // A single transient admiral hiccup must not permanently surface this entry,
                // because surfacing sets AuditCriticalTrigger and the entry-level guard would
                // then block any future merge_queue.failed event from retrying. Track
                // consecutive admiral failures per mission; only surface once the threshold
                // is crossed. The counter is cleared on the next successful redispatch (see
                // post-try block) so a single recovery resets the count.
                int failures = _AdmiralDispatchFailureCounts.AddOrUpdate(mission.Id, 1, (_, current) => current + 1);
                if (failures < _AdmiralDispatchFailureThreshold)
                {
                    _Logging.Warn(_Header + "admiral dispatch failure " + failures + "/" + _AdmiralDispatchFailureThreshold
                        + " for mission " + mission.Id + " (entry " + entry.Id + "); deferring surface so a future merge_queue.failed event can retry");
                    return;
                }

                _Logging.Error(_Header + "admiral dispatch threshold reached (" + failures + "/" + _AdmiralDispatchFailureThreshold
                    + ") for mission " + mission.Id + "; surfacing entry " + entry.Id + " as " + _RecoveryDispatchFailedReason);
                _AdmiralDispatchFailureCounts.TryRemove(mission.Id, out _);
                await SurfaceEntryAsync(entry, _RecoveryDispatchFailedReason, token).ConfigureAwait(false);
                await EmitRecoveryEventAsync(entry, mission, "Surface", _RecoveryDispatchFailedReason, previousAttempts, failureClass, token).ConfigureAwait(false);
                return;
            }

            // Successful admiral dispatch: reset the consecutive-failure counter so a later
            // hiccup starts from zero rather than carrying over a stale count.
            _AdmiralDispatchFailureCounts.TryRemove(mission.Id, out _);

            // Mark the entry Cancelled with a reason; the redispatched mission will create a
            // fresh merge entry when its work lands.
            entry.Status = MergeStatusEnum.Cancelled;
            entry.TestOutput = "recovery_redispatched";
            entry.CompletedUtc = DateTime.UtcNow;
            entry.LastUpdateUtc = DateTime.UtcNow;
            await _Database.MergeEntries.UpdateAsync(entry, token).ConfigureAwait(false);

            await EmitRecoveryEventAsync(entry, mission, "Redispatch", null, mission.RecoveryAttempts, failureClass, token).ConfigureAwait(false);
        }

        private async Task ExecuteSurfaceAsync(MergeEntry entry, string reason, Mission mission, MergeFailureClassEnum failureClass, CancellationToken token)
        {
            await SurfaceEntryAsync(entry, reason, token).ConfigureAwait(false);
            await EmitRecoveryEventAsync(entry, mission, "Surface", reason, mission.RecoveryAttempts, failureClass, token).ConfigureAwait(false);
        }

        private async Task SurfaceEntryAsync(MergeEntry entry, string reason, CancellationToken token)
        {
            entry.AuditCriticalTrigger = reason;
            string classifierLine = "recovery_surface: " + reason
                + " (class=" + (entry.MergeFailureClass?.ToString() ?? "Unknown") + ", summary="
                + (entry.MergeFailureSummary ?? String.Empty) + ")";
            entry.AuditDeepNotes = String.IsNullOrEmpty(entry.AuditDeepNotes)
                ? classifierLine
                : (entry.AuditDeepNotes + "\n" + classifierLine);
            entry.LastUpdateUtc = DateTime.UtcNow;
            await _Database.MergeEntries.UpdateAsync(entry, token).ConfigureAwait(false);
        }

        private async Task EmitRecoveryEventAsync(
            MergeEntry entry,
            Mission mission,
            string action,
            string? reason,
            int recoveryAttempts,
            MergeFailureClassEnum failureClass,
            CancellationToken token)
        {
            try
            {
                ArmadaEvent evt = new ArmadaEvent(
                    "merge_queue.recovery_action",
                    "Recovery action " + action + " for merge entry " + entry.Id);
                evt.EntityType = "merge_entry";
                evt.EntityId = entry.Id;
                evt.MissionId = mission.Id;
                evt.VesselId = mission.VesselId;
                evt.VoyageId = mission.VoyageId;
                evt.CaptainId = mission.CaptainId;
                evt.Payload = JsonSerializer.Serialize(new
                {
                    action = action,
                    reason = reason,
                    recoveryAttempts = recoveryAttempts,
                    failureClass = failureClass.ToString(),
                    entryId = entry.Id,
                    missionId = mission.Id
                });
                await _Database.Events.CreateAsync(evt, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "error emitting merge_queue.recovery_action event for " + entry.Id + ": " + ex.Message);
            }
        }

        /// <summary>
        /// Parse the JSON-array conflicted-files payload and return its element count.
        /// Returns 0 on null/empty/parse-failure -- the router treats 0 as "trivially small".
        /// </summary>
        private static int ParseConflictedFileCount(string? conflictedFilesJson)
        {
            if (String.IsNullOrEmpty(conflictedFilesJson)) return 0;
            try
            {
                using (JsonDocument doc = JsonDocument.Parse(conflictedFilesJson))
                {
                    if (doc.RootElement.ValueKind != JsonValueKind.Array) return 0;
                    return doc.RootElement.GetArrayLength();
                }
            }
            catch
            {
                return 0;
            }
        }

        #endregion
    }
}
