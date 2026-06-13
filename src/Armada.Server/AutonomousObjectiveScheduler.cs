namespace Armada.Server
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using SyslogLogging;

    /// <summary>
    /// Background sweep service that selects eligible objectives, applies guardrails,
    /// auto-dispatches each through AdmiralService, links the resulting voyage, and
    /// reconciles objectives whose linked voyage has completed to Completed status.
    /// </summary>
    public sealed class AutonomousObjectiveScheduler
    {
        #region Public-Members

        /// <summary>
        /// Whether the scheduler is allowed to auto-dispatch eligible objectives.
        /// </summary>
        public bool Enabled { get; private set; }

        /// <summary>
        /// Whether the scheduler is temporarily paused.
        /// </summary>
        public bool Paused { get; private set; }

        /// <summary>
        /// Minutes between scheduled sweep ticks.
        /// </summary>
        public int IntervalMinutes { get; private set; }

        /// <summary>
        /// Maximum number of objectives with simultaneously active linked voyages.
        /// </summary>
        public int MaxConcurrentVoyages { get; private set; }

        /// <summary>
        /// UTC timestamp of the last completed sweep tick, or null if no tick has run.
        /// </summary>
        public DateTime? LastTickUtc { get; private set; }

        /// <summary>
        /// Short plain-text summary of the last sweep result.
        /// </summary>
        public string? LastResultSummary { get; private set; }

        /// <summary>
        /// Number of objectives with currently active linked voyages, as of the last sweep tick.
        /// </summary>
        public int ActiveDispatchedCount { get; private set; }

        /// <summary>
        /// Reason the last sweep skipped dispatch (e.g. "disabled", "paused", "max_concurrent"),
        /// or null if the last sweep dispatched normally.
        /// </summary>
        public string? LastSkipReason { get; private set; }

        #endregion

        #region Private-Members

        private const string _Header = "[AutonomousObjectiveScheduler] ";
        private readonly DatabaseDriver _Database;
        private readonly ObjectiveService _Objectives;
        private readonly IAdmiralService _Admiral;
        private readonly IMergeQueueService _MergeQueue;
        private readonly ArmadaSettings _Settings;
        private readonly LoggingModule _Logging;
        private readonly ICodeIndexService? _CodeIndex;
        private readonly SemaphoreSlim _SweepLock = new SemaphoreSlim(1, 1);

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate the scheduler with required dependencies.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="objectives">Objective service.</param>
        /// <param name="admiral">Admiral service for voyage dispatch.</param>
        /// <param name="mergeQueue">Merge queue service for back-pressure gating.</param>
        /// <param name="settings">Armada settings (seed values for runtime state).</param>
        /// <param name="logging">Logging module.</param>
        /// <param name="codeIndex">Optional code index service for index-update gating.</param>
        public AutonomousObjectiveScheduler(
            DatabaseDriver database,
            ObjectiveService objectives,
            IAdmiralService admiral,
            IMergeQueueService mergeQueue,
            ArmadaSettings settings,
            LoggingModule logging,
            ICodeIndexService? codeIndex = null)
        {
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _Objectives = objectives ?? throw new ArgumentNullException(nameof(objectives));
            _Admiral = admiral ?? throw new ArgumentNullException(nameof(admiral));
            _MergeQueue = mergeQueue ?? throw new ArgumentNullException(nameof(mergeQueue));
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
            _CodeIndex = codeIndex;

            Enabled = settings.AutonomousObjectiveScheduler.Enabled;
            Paused = settings.AutonomousObjectiveScheduler.Paused;
            IntervalMinutes = settings.AutonomousObjectiveScheduler.IntervalMinutes;
            MaxConcurrentVoyages = settings.AutonomousObjectiveScheduler.MaxConcurrentVoyages;
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Allow the scheduler to dispatch objectives on subsequent sweeps.
        /// </summary>
        public void Enable() => Enabled = true;

        /// <summary>
        /// Prevent the scheduler from dispatching objectives.
        /// </summary>
        public void Disable() => Enabled = false;

        /// <summary>
        /// Temporarily suspend dispatch without clearing the Enabled flag.
        /// </summary>
        public void Pause() => Paused = true;

        /// <summary>
        /// Resume from a paused state.
        /// </summary>
        public void Resume() => Paused = false;

        /// <summary>
        /// Set the sweep interval, clamped to 1-1440 minutes.
        /// </summary>
        /// <param name="minutes">New interval in minutes.</param>
        public void SetIntervalMinutes(int minutes)
        {
            IntervalMinutes = Math.Max(1, Math.Min(1440, minutes));
        }

        /// <summary>
        /// Set the maximum number of concurrently active objective voyages, clamped to 1-50.
        /// </summary>
        /// <param name="max">New concurrency cap.</param>
        public void SetMaxConcurrentVoyages(int max)
        {
            MaxConcurrentVoyages = Math.Max(1, Math.Min(50, max));
        }

        /// <summary>
        /// Fire-and-forget background sweep. The caller is never blocked.
        /// OperationCanceledException is swallowed silently; all other errors are logged as warnings.
        /// </summary>
        /// <param name="token">Cancellation token.</param>
        public void TriggerBackgroundSweep(CancellationToken token = default)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await SweepAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    _Logging.Warn(_Header + "background sweep failed: " + ex.Message);
                }
            }, CancellationToken.None);
        }

        /// <summary>
        /// Run one bounded scheduling sweep: reconcile completed objectives, then dispatch eligible ones.
        /// Non-reentrant; concurrent calls return immediately without running a second sweep.
        /// Skips the work portion if the last tick ran within IntervalMinutes, so the health loop
        /// can call this on every heartbeat without over-triggering.
        /// </summary>
        /// <param name="token">Cancellation token.</param>
        public async Task SweepAsync(CancellationToken token = default)
        {
            if (!await _SweepLock.WaitAsync(0, token).ConfigureAwait(false)) return;

            try
            {
                if (LastTickUtc.HasValue
                    && (DateTime.UtcNow - LastTickUtc.Value).TotalMinutes < IntervalMinutes)
                {
                    _Logging.Debug(_Header + "sweep skipped: interval not elapsed (" + IntervalMinutes + " min).");
                    return;
                }

                LastTickUtc = DateTime.UtcNow;

                if (!Enabled)
                {
                    _Logging.Debug(_Header + "sweep skipped: scheduler is disabled.");
                    await EmitSystemEventAsync("objective_scheduler.skipped_disabled",
                        "Autonomous objective scheduler sweep skipped: scheduler is disabled.", token).ConfigureAwait(false);
                    LastSkipReason = "disabled";
                    LastResultSummary = "skipped (disabled)";
                    return;
                }

                if (Paused)
                {
                    _Logging.Debug(_Header + "sweep skipped: scheduler is paused.");
                    await EmitSystemEventAsync("objective_scheduler.skipped_paused",
                        "Autonomous objective scheduler sweep skipped: scheduler is paused.", token).ConfigureAwait(false);
                    LastSkipReason = "paused";
                    LastResultSummary = "skipped (paused)";
                    return;
                }

                AuthContext systemAuth = BuildSystemAuth();
                List<Objective> snapshot = await ReadAllObjectivesAsync(systemAuth, token).ConfigureAwait(false);

                int reconciledCount = await ReconcileCompletedObjectivesAsync(systemAuth, snapshot, token).ConfigureAwait(false);

                snapshot = await ReadAllObjectivesAsync(systemAuth, token).ConfigureAwait(false);
                List<Objective> eligible = AutonomousObjectiveSelector.SelectEligible(snapshot);

                int activeCount = CountActiveDispatched(snapshot);
                ActiveDispatchedCount = activeCount;
                int capacity = MaxConcurrentVoyages - activeCount;

                if (capacity <= 0)
                {
                    _Logging.Debug(_Header + "sweep: max concurrent voyages reached (" + activeCount + "/" + MaxConcurrentVoyages + ").");
                    await EmitSystemEventAsync("objective_scheduler.skipped_max_concurrent",
                        "Autonomous objective scheduler dispatch skipped: max concurrent voyages reached (" + activeCount + "/" + MaxConcurrentVoyages + ").", token).ConfigureAwait(false);
                    LastSkipReason = "max_concurrent";
                    LastResultSummary = "reconciled=" + reconciledCount + " dispatched=0 (max_concurrent)";
                    return;
                }

                int dispatched = 0;
                List<MergeEntry> mergeQueue = await _MergeQueue.ListAsync(token: token).ConfigureAwait(false);

                foreach (Objective objective in eligible)
                {
                    if (dispatched >= capacity) break;
                    token.ThrowIfCancellationRequested();

                    try
                    {
                        await DispatchObjectiveAsync(objective, mergeQueue, token).ConfigureAwait(false);
                        dispatched++;
                    }
                    catch (ObjectiveSkippedException)
                    {
                    }
                    catch (Exception ex)
                    {
                        _Logging.Warn(_Header + "dispatch failed for objective " + objective.Id + ": " + ex.Message);
                    }
                }

                LastSkipReason = null;
                LastResultSummary = "reconciled=" + reconciledCount + " dispatched=" + dispatched;
                _Logging.Info(_Header + "sweep complete: reconciled=" + reconciledCount + " dispatched=" + dispatched + " capacity=" + capacity + ".");
            }
            finally
            {
                _SweepLock.Release();
            }
        }

        #endregion

        #region Private-Methods

        private async Task<int> ReconcileCompletedObjectivesAsync(AuthContext systemAuth, List<Objective> snapshot, CancellationToken token)
        {
            int reconciled = 0;
            List<Objective> inProgress = snapshot
                .Where(o => o.Status == ObjectiveStatusEnum.InProgress && o.VoyageIds.Count > 0)
                .ToList();

            foreach (Objective objective in inProgress)
            {
                try
                {
                    bool allLanded = await AllLinkedVoyagesCompletedAsync(objective, token).ConfigureAwait(false);
                    if (!allLanded) continue;

                    AuthContext objectiveAuth = BuildAuth(objective);
                    ObjectiveUpsertRequest req = new ObjectiveUpsertRequest
                    {
                        Title = objective.Title,
                        Status = ObjectiveStatusEnum.Completed
                    };
                    await _Objectives.UpdateAsync(objectiveAuth, objective.Id, req, token).ConfigureAwait(false);

                    await EmitObjectiveEventAsync("objective_scheduler.objective_completed",
                        "Autonomous scheduler reconciled objective " + objective.Id + " to Completed: all linked voyages landed.",
                        objective, null, token).ConfigureAwait(false);

                    reconciled++;
                }
                catch (Exception ex)
                {
                    _Logging.Warn(_Header + "reconcile failed for objective " + objective.Id + ": " + ex.Message);
                }
            }

            return reconciled;
        }

        private async Task<bool> AllLinkedVoyagesCompletedAsync(Objective objective, CancellationToken token)
        {
            foreach (string voyageId in objective.VoyageIds)
            {
                Voyage? voyage = await _Database.Voyages.ReadAsync(voyageId, token).ConfigureAwait(false);
                if (voyage == null) continue;
                if (voyage.Status != VoyageStatusEnum.Complete) return false;
            }

            return true;
        }

        private static int CountActiveDispatched(List<Objective> snapshot)
        {
            return snapshot.Count(o =>
                o.Status == ObjectiveStatusEnum.InProgress
                && o.VoyageIds.Count > 0);
        }

        private async Task DispatchObjectiveAsync(
            Objective objective,
            List<MergeEntry> mergeQueue,
            CancellationToken token)
        {
            if (objective.VesselIds.Count != 1)
            {
                _Logging.Warn(_Header + "objective " + objective.Id + " skipped: must have exactly one vessel for v1 auto-dispatch (has " + objective.VesselIds.Count + ").");
                throw new ObjectiveSkippedException();
            }

            string vesselId = objective.VesselIds[0];

            bool hasBackPressure = mergeQueue.Any(e =>
                String.Equals(e.VesselId, vesselId, StringComparison.OrdinalIgnoreCase)
                && !IsMergeTerminal(e.Status));

            if (hasBackPressure)
            {
                _Logging.Debug(_Header + "objective " + objective.Id + " skipped: merge queue back-pressure for vessel " + vesselId + ".");
                await EmitObjectiveEventAsync("objective_scheduler.skipped_backpressure",
                    "Autonomous scheduler skipped objective " + objective.Id + ": merge queue back-pressure for vessel " + vesselId + ".",
                    objective, vesselId, token).ConfigureAwait(false);
                throw new ObjectiveSkippedException();
            }

            if (_CodeIndex != null)
            {
                try
                {
                    CodeIndexStatus indexStatus = await _CodeIndex.GetStatusAsync(vesselId, token).ConfigureAwait(false);
                    if (indexStatus.UpdateInProgress)
                    {
                        _Logging.Debug(_Header + "objective " + objective.Id + " skipped: code index update in progress for vessel " + vesselId + ".");
                        await EmitObjectiveEventAsync("objective_scheduler.skipped_index_update",
                            "Autonomous scheduler skipped objective " + objective.Id + ": code index update in progress for vessel " + vesselId + ".",
                            objective, vesselId, token).ConfigureAwait(false);
                        throw new ObjectiveSkippedException();
                    }
                }
                catch (ObjectiveSkippedException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _Logging.Warn(_Header + "could not read code index status for vessel " + vesselId + ", proceeding without index gate: " + ex.Message);
                }
            }

            string missionDescription = BuildMissionDescription(objective);
            MissionDescription md = new MissionDescription(objective.Title, missionDescription)
            {
                CodeContextMode = "auto"
            };

            List<MissionDescription> missionDescriptions = new List<MissionDescription> { md };

            Voyage voyage = await _Admiral.DispatchVoyageAsync(
                objective.Title,
                missionDescription,
                vesselId,
                missionDescriptions,
                objective.SuggestedPipelineId,
                objective.SuggestedPlaybooks.Count > 0 ? objective.SuggestedPlaybooks : null,
                token).ConfigureAwait(false);

            AuthContext objectiveAuth = BuildAuth(objective);
            await _Objectives.LinkVoyageAsync(objectiveAuth, objective.Id, voyage.Id, token).ConfigureAwait(false);

            await EmitObjectiveEventAsync("objective_scheduler.objective_dispatched",
                "Autonomous scheduler dispatched objective " + objective.Id + " as voyage " + voyage.Id + " on vessel " + vesselId + ".",
                objective, vesselId, token).ConfigureAwait(false);

            _Logging.Info(_Header + "dispatched objective " + objective.Id + " as voyage " + voyage.Id + " on vessel " + vesselId + ".");
        }

        private static string BuildMissionDescription(Objective objective)
        {
            StringBuilder sb = new StringBuilder();
            if (!String.IsNullOrWhiteSpace(objective.Description))
            {
                sb.AppendLine(objective.Description.Trim());
                sb.AppendLine();
            }

            if (objective.AcceptanceCriteria.Count > 0)
            {
                sb.AppendLine("## Acceptance Criteria");
                foreach (string criterion in objective.AcceptanceCriteria)
                    sb.AppendLine("- " + criterion);
                sb.AppendLine();
            }

            if (objective.NonGoals.Count > 0)
            {
                sb.AppendLine("## Non-Goals");
                foreach (string nonGoal in objective.NonGoals)
                    sb.AppendLine("- " + nonGoal);
                sb.AppendLine();
            }

            return sb.ToString().TrimEnd();
        }

        private static bool IsMergeTerminal(MergeStatusEnum status)
        {
            return status == MergeStatusEnum.Landed
                || status == MergeStatusEnum.Failed
                || status == MergeStatusEnum.Cancelled;
        }

        private async Task EmitSystemEventAsync(string eventType, string message, CancellationToken token)
        {
            try
            {
                ArmadaEvent evt = new ArmadaEvent(eventType, message)
                {
                    EntityType = "scheduler",
                    EntityId = "autonomous_objective_scheduler"
                };
                await _Database.Events.CreateAsync(evt, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "failed to emit event " + eventType + ": " + ex.Message);
            }
        }

        private async Task EmitObjectiveEventAsync(string eventType, string message, Objective objective, string? vesselId, CancellationToken token)
        {
            try
            {
                ArmadaEvent evt = new ArmadaEvent(eventType, message)
                {
                    TenantId = objective.TenantId,
                    UserId = objective.UserId,
                    EntityType = "objective",
                    EntityId = objective.Id,
                    VesselId = vesselId
                };
                await _Database.Events.CreateAsync(evt, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "failed to emit event " + eventType + " for objective " + objective.Id + ": " + ex.Message);
            }
        }

        private async Task<List<Objective>> ReadAllObjectivesAsync(AuthContext auth, CancellationToken token)
        {
            List<Objective> all = new List<Objective>();
            int pageNumber = 1;
            const int pageSize = 500;
            while (true)
            {
                EnumerationResult<Objective> page = await _Objectives.EnumerateAsync(auth, new ObjectiveQuery
                {
                    PageNumber = pageNumber,
                    PageSize = pageSize
                }, token).ConfigureAwait(false);
                all.AddRange(page.Objects);
                if (page.Objects.Count < pageSize) break;
                pageNumber++;
            }

            return all;
        }

        private static AuthContext BuildSystemAuth()
        {
            return AuthContext.Authenticated(
                Constants.DefaultTenantId,
                Constants.DefaultUserId,
                true,
                true,
                "AutonomousObjectiveScheduler",
                principalDisplay: "Armada Autonomous Objective Scheduler");
        }

        private static AuthContext BuildAuth(Objective objective)
        {
            return AuthContext.Authenticated(
                objective.TenantId ?? Constants.DefaultTenantId,
                objective.UserId ?? Constants.DefaultUserId,
                false,
                true,
                "AutonomousObjectiveScheduler",
                principalDisplay: "Armada Autonomous Objective Scheduler");
        }

        private sealed class ObjectiveSkippedException : Exception
        {
            /// <summary>
            /// Instantiate.
            /// </summary>
            public ObjectiveSkippedException() : base("objective skipped") { }
        }

        #endregion
    }
}
