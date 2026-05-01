namespace Armada.Core.Recovery
{
    using System;
    using System.Collections.Generic;
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
    /// executes the chosen terminal action: redispatch the original mission, create a
    /// rebase-captain mission, or surface to the PR-fallback channel for human
    /// resolution. The handler increments the owning mission's recovery-attempt
    /// counter on every action so the router observes back-pressure on subsequent
    /// failures.
    /// </summary>
    public sealed class MergeRecoveryHandler : IMergeRecoveryHandler
    {
        #region Private-Members

        /// <summary>Header used for log lines.</summary>
        private const string _Header = "[MergeRecoveryHandler] ";

        /// <summary>Triviality threshold: 1 file and a modest diff lets a redispatch try.</summary>
        private const int _TrivialMaxConflictedFiles = 1;
        private const int _TrivialMaxDiffLineCount = 60;

        private readonly LoggingModule _Logging;
        private readonly DatabaseDriver _Database;
        private readonly ArmadaSettings _Settings;
        private readonly IRecoveryRouter _Router;
        private readonly IRebaseCaptainDockSetup _DockSetup;
        private readonly IMergeQueueService _MergeQueue;
        private readonly IPlaybookService? _Playbooks;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="logging">Logging module.</param>
        /// <param name="database">Database driver for reading the failed entry/mission and
        /// persisting the rebase-captain mission row.</param>
        /// <param name="settings">Application settings (recovery cap).</param>
        /// <param name="router">Pure router for action selection.</param>
        /// <param name="dockSetup">Builds the rebase-captain mission spec.</param>
        /// <param name="mergeQueue">Merge-queue service used for the recovery-exhausted
        /// PR-fallback hook.</param>
        /// <param name="playbooks">Optional playbook service. When provided, the handler
        /// materializes <see cref="MissionPlaybookSnapshot"/> rows for the rebase mission so
        /// the inline <c>pbk_rebase_captain</c> body actually reaches the dispatched
        /// captain. Tests that do not exercise the playbook surface may pass null.</param>
        public MergeRecoveryHandler(
            LoggingModule logging,
            DatabaseDriver database,
            ArmadaSettings settings,
            IRecoveryRouter router,
            IRebaseCaptainDockSetup dockSetup,
            IMergeQueueService mergeQueue,
            IPlaybookService? playbooks = null)
        {
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _Router = router ?? throw new ArgumentNullException(nameof(router));
            _DockSetup = dockSetup ?? throw new ArgumentNullException(nameof(dockSetup));
            _MergeQueue = mergeQueue ?? throw new ArgumentNullException(nameof(mergeQueue));
            _Playbooks = playbooks;
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
                _Logging.Warn(_Header + "merge entry not found: " + mergeEntryId);
                return;
            }
            if (entry.Status != MergeStatusEnum.Failed)
            {
                _Logging.Debug(_Header + "skipping non-failed entry " + mergeEntryId + " status=" + entry.Status);
                return;
            }
            if (!entry.MergeFailureClass.HasValue)
            {
                _Logging.Warn(_Header + "entry " + mergeEntryId + " has no classification; surfacing");
                await SurfaceAsync(entry, "classification_missing", token).ConfigureAwait(false);
                return;
            }
            if (String.IsNullOrEmpty(entry.MissionId))
            {
                _Logging.Warn(_Header + "entry " + mergeEntryId + " has no mission; surfacing");
                await SurfaceAsync(entry, "mission_missing", token).ConfigureAwait(false);
                return;
            }

            Mission? mission = await _Database.Missions.ReadAsync(entry.MissionId, token).ConfigureAwait(false);
            if (mission == null)
            {
                _Logging.Warn(_Header + "mission not found for entry " + mergeEntryId);
                await SurfaceAsync(entry, "mission_missing", token).ConfigureAwait(false);
                return;
            }

            MergeFailureClassification classification = ReconstructClassification(entry);
            bool trivial = IsConflictTrivial(entry, classification);
            RecoveryAction action = _Router.Route(classification.FailureClass, trivial, mission.RecoveryAttempts);

            _Logging.Info(_Header + "entry " + entry.Id + " mission=" + mission.Id
                + " class=" + classification.FailureClass
                + " trivial=" + trivial
                + " attempts=" + mission.RecoveryAttempts
                + " -> " + action.GetType().Name);

            switch (action)
            {
                case RecoveryAction.Redispatch:
                    await HandleRedispatchAsync(entry, mission, token).ConfigureAwait(false);
                    break;

                case RecoveryAction.RebaseCaptain:
                    await HandleRebaseCaptainAsync(entry, mission, classification, token).ConfigureAwait(false);
                    break;

                case RecoveryAction.Surface surface:
                    await SurfaceAsync(entry, surface.Reason, token).ConfigureAwait(false);
                    break;
            }
        }

        #endregion

        #region Private-Methods

        private async Task HandleRebaseCaptainAsync(
            MergeEntry entry,
            Mission mission,
            MergeFailureClassification classification,
            CancellationToken token)
        {
            // 1. Increment the failed mission's recovery counter BEFORE building the
            //    spec. If BuildAsync or mission creation throws, the counter is already
            //    persisted and we surface as recovery_unstartable -- never burn another
            //    recovery slot on an unstartable rebase.
            mission.RecoveryAttempts++;
            mission.LastRecoveryActionUtc = DateTime.UtcNow;
            mission.LastUpdateUtc = DateTime.UtcNow;
            try
            {
                await _Database.Missions.UpdateAsync(mission, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "failed to persist recovery counter for mission " + mission.Id + ": " + ex.Message);
                // Do not throw -- continue so the operator still gets a surface.
            }

            RebaseCaptainMissionSpec spec;
            try
            {
                spec = await _DockSetup.BuildAsync(entry, mission, classification, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "BuildAsync failed for mission " + mission.Id + ": " + ex.Message);
                await SurfaceAsync(entry, "recovery_unstartable", token).ConfigureAwait(false);
                return;
            }

            Mission rebase = new Mission
            {
                Title = "Rebase captain: " + mission.Title,
                Description = spec.Brief,
                Persona = "Worker",
                VesselId = mission.VesselId,
                VoyageId = mission.VoyageId,
                ParentMissionId = mission.Id,
                BranchName = spec.LandingTargetBranch,
                Status = MissionStatusEnum.Pending,
                Priority = mission.Priority,
                PreferredModel = spec.PreferredModel,
                DependsOnMissionId = spec.DependsOnMissionId,
                SelectedPlaybooks = new List<SelectedPlaybook>(spec.SelectedPlaybooks),
                PrestagedFiles = new List<PrestagedFile>(spec.PrestagedFiles),
                RecoveryAttempts = spec.RecoveryAttempts,
                TenantId = mission.TenantId,
                UserId = mission.UserId
            };

            try
            {
                await _Database.Missions.CreateAsync(rebase, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "CreateAsync failed for rebase mission of " + mission.Id + ": " + ex.Message);
                await SurfaceAsync(entry, "recovery_unstartable", token).ConfigureAwait(false);
                return;
            }

            // Materialize playbook snapshots so the inline pbk_rebase_captain body actually
            // reaches the dispatched captain. AdmiralService.DispatchMissionAsync would do
            // this on a fresh dispatch, but the recovery handler creates the mission row
            // directly to avoid the prestaged-file validation rejection that would fire
            // for the synthesized conflict-state marker. Best-effort: a snapshot persist
            // failure does not unwind the rebase mission row.
            await PersistPlaybookSnapshotsAsync(rebase, token).ConfigureAwait(false);

            entry.Status = MergeStatusEnum.Cancelled;
            entry.TestOutput = "recovery_rebased";
            entry.LastUpdateUtc = DateTime.UtcNow;
            entry.CompletedUtc = DateTime.UtcNow;
            try
            {
                await _Database.MergeEntries.UpdateAsync(entry, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "failed to mark entry " + entry.Id + " as recovery_rebased: " + ex.Message);
            }

            _Logging.Info(_Header + "rebase mission " + rebase.Id + " created for failed mission " + mission.Id);
        }

        private async Task HandleRedispatchAsync(MergeEntry entry, Mission mission, CancellationToken token)
        {
            // Increment counter and bookkeeping on the mission. Redispatch reuses the
            // existing mission id; orchestrator-level restart logic walks Pending state.
            mission.RecoveryAttempts++;
            mission.LastRecoveryActionUtc = DateTime.UtcNow;
            mission.Status = MissionStatusEnum.Pending;
            mission.CompletedUtc = null;
            mission.LastUpdateUtc = DateTime.UtcNow;
            try
            {
                await _Database.Missions.UpdateAsync(mission, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "failed to redispatch mission " + mission.Id + ": " + ex.Message);
                await SurfaceAsync(entry, "recovery_unstartable", token).ConfigureAwait(false);
                return;
            }

            entry.Status = MergeStatusEnum.Cancelled;
            entry.TestOutput = "recovery_redispatched";
            entry.LastUpdateUtc = DateTime.UtcNow;
            entry.CompletedUtc = DateTime.UtcNow;
            try
            {
                await _Database.MergeEntries.UpdateAsync(entry, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "failed to mark entry " + entry.Id + " as recovery_redispatched: " + ex.Message);
            }
        }

        private async Task SurfaceAsync(MergeEntry entry, string reason, CancellationToken token)
        {
            entry.AuditCriticalTrigger = reason;
            entry.LastUpdateUtc = DateTime.UtcNow;
            try
            {
                await _Database.MergeEntries.UpdateAsync(entry, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "failed to surface entry " + entry.Id + ": " + ex.Message);
                return;
            }

            _Logging.Info(_Header + "entry " + entry.Id + " surfaced: " + reason);

            // Recovery-exhausted hook: re-poke the merge queue so the existing PR-fallback
            // path opens a real platform PR. The merge-queue service interprets a
            // non-empty AuditCriticalTrigger as "route to PR" when it processes the
            // entry, so the open-PR call must run through the dedicated recovery
            // surface on IMergeQueueService rather than re-enqueuing the entry.
            if (String.Equals(reason, "recovery_exhausted", StringComparison.Ordinal))
            {
                try
                {
                    await _MergeQueue.TryOpenPullRequestForRecoveryAsync(entry.Id, token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _Logging.Warn(_Header + "PR-fallback re-poke failed for entry " + entry.Id + ": " + ex.Message);
                }
            }
        }

        private async Task PersistPlaybookSnapshotsAsync(Mission rebase, CancellationToken token)
        {
            if (rebase.SelectedPlaybooks == null || rebase.SelectedPlaybooks.Count == 0) return;

            try
            {
                List<MissionPlaybookSnapshot> snapshots;
                if (_Playbooks != null && !String.IsNullOrEmpty(rebase.TenantId))
                {
                    snapshots = await _Playbooks.CreateSnapshotsAsync(rebase.TenantId, rebase.SelectedPlaybooks, token).ConfigureAwait(false);
                }
                else
                {
                    snapshots = BuildInlineOnlySnapshots(rebase.SelectedPlaybooks);
                }

                if (snapshots.Count == 0) return;

                rebase.PlaybookSnapshots = snapshots;
                await _Database.Playbooks.SetMissionSnapshotsAsync(rebase.Id, snapshots, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "playbook snapshot persistence failed for rebase mission " + rebase.Id + ": " + ex.Message);
            }
        }

        private static List<MissionPlaybookSnapshot> BuildInlineOnlySnapshots(List<SelectedPlaybook> selections)
        {
            List<MissionPlaybookSnapshot> snapshots = new List<MissionPlaybookSnapshot>();
            foreach (SelectedPlaybook selection in selections)
            {
                if (selection == null || String.IsNullOrEmpty(selection.InlineFullContent)) continue;
                snapshots.Add(new MissionPlaybookSnapshot
                {
                    PlaybookId = selection.PlaybookId,
                    FileName = (selection.PlaybookId ?? "playbook") + ".md",
                    Description = null,
                    Content = selection.InlineFullContent!,
                    DeliveryMode = selection.DeliveryMode,
                    SourceLastUpdateUtc = null
                });
            }
            return snapshots;
        }

        private static MergeFailureClassification ReconstructClassification(MergeEntry entry)
        {
            IReadOnlyList<string> conflicted = ParseConflictedFiles(entry.ConflictedFiles);
            return new MergeFailureClassification(
                FailureClass: entry.MergeFailureClass!.Value,
                Summary: entry.MergeFailureSummary ?? string.Empty,
                ConflictedFiles: conflicted);
        }

        private static IReadOnlyList<string> ParseConflictedFiles(string? json)
        {
            if (String.IsNullOrEmpty(json)) return Array.Empty<string>();
            try
            {
                List<string>? parsed = JsonSerializer.Deserialize<List<string>>(json);
                return parsed != null ? parsed : Array.Empty<string>();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        private static bool IsConflictTrivial(MergeEntry entry, MergeFailureClassification classification)
        {
            int conflictedFileCount = classification.ConflictedFiles?.Count ?? 0;
            if (conflictedFileCount > _TrivialMaxConflictedFiles) return false;
            if (entry.DiffLineCount > _TrivialMaxDiffLineCount) return false;
            return true;
        }

        #endregion
    }
}
