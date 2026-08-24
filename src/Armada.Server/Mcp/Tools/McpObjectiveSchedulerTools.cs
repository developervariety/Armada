namespace Armada.Server.Mcp.Tools
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;

    /// <summary>
    /// Registers MCP tools for controlling and inspecting the autonomous objective scheduler.
    /// </summary>
    public static class McpObjectiveSchedulerTools
    {
        #region Public-Members

        #endregion

        #region Private-Members

        private static readonly JsonSerializerOptions _JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter() }
        };

        #endregion

        #region Public-Methods

        /// <summary>
        /// Registers the objective scheduler MCP tools.
        /// </summary>
        /// <param name="register">Tool registration delegate.</param>
        /// <param name="scheduler">Autonomous objective scheduler instance.</param>
        /// <param name="database">Database driver for objective persistence.</param>
        /// <param name="objectiveService">Objective service for updating objectives.</param>
        /// <param name="coordination">
        /// Coordination service for presence and board notes. When supplied, the stale-pause clear
        /// tool is registered; without it the tool is absent, because a clear that cannot be
        /// announced is not acceptable.
        /// </param>
        public static void Register(
            RegisterToolDelegate register,
            AutonomousObjectiveScheduler scheduler,
            DatabaseDriver database,
            ObjectiveService objectiveService,
            CoordinationService? coordination = null)
        {
            if (register == null) throw new ArgumentNullException(nameof(register));
            if (scheduler == null) throw new ArgumentNullException(nameof(scheduler));
            if (database == null) throw new ArgumentNullException(nameof(database));
            if (objectiveService == null) throw new ArgumentNullException(nameof(objectiveService));

            register(
                "armada_objective_scheduler_status",
                "Get the current runtime status of the autonomous objective scheduler.",
                new { type = "object", properties = new { } },
                async (args) =>
                {
                    await Task.CompletedTask.ConfigureAwait(false);
                    return (object)BuildStatus(scheduler);
                });

            register(
                "armada_objective_scheduler_set",
                "Enable, disable, pause, or adjust the autonomous objective scheduler. All fields are optional; omitted fields leave the current value unchanged.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        enabled = new { type = "boolean", description = "When true the scheduler dispatches eligible objectives; when false it is fully disabled." },
                        paused = new { type = "boolean", description = "When true the scheduler is suspended without clearing the enabled flag. Name your session in pausedBy and say why in pauseReason; a pause without an owner can only be cleared by an operator." },
                        pausedBy = new { type = "string", description = "Your session participant key, recorded on the pause so a departed session's pause can be recognised as stale. Strongly recommended when paused is true." },
                        pauseReason = new { type = "string", description = "Why the scheduler is being paused, for example the deploy it protects." },
                        intervalMinutes = new { type = "integer", description = "Sweep interval in minutes (clamped to 1-1440)." },
                        maxConcurrentVoyages = new { type = "integer", description = "Fleet-wide active objective voyage ceiling (clamped to 1-50). Operator-dispatched linked voyages count toward it." },
                        maxConcurrentVoyagesPerVessel = new { type = "integer", description = "Per-vessel active objective voyage ceiling (clamped to 1-50, default 1). This prevents one vessel from consuming the fleet-wide capacity." }
                    }
                },
                async (args) =>
                {
                    SchedulerSetArgs? request = args.HasValue
                        ? JsonSerializer.Deserialize<SchedulerSetArgs>(args.Value, _JsonOptions)
                        : null;

                    if (request != null)
                    {
                        if (request.Enabled.HasValue)
                        {
                            if (request.Enabled.Value) scheduler.Enable();
                            else scheduler.Disable();
                        }

                        if (request.Paused.HasValue)
                        {
                            if (request.Paused.Value) scheduler.Pause(request.PausedBy, request.PauseReason);
                            else scheduler.Resume();
                        }

                        if (request.IntervalMinutes.HasValue)
                            scheduler.SetIntervalMinutes(request.IntervalMinutes.Value);

                        if (request.MaxConcurrentVoyages.HasValue)
                            scheduler.SetMaxConcurrentVoyages(request.MaxConcurrentVoyages.Value);

                        if (request.MaxConcurrentVoyagesPerVessel.HasValue)
                            scheduler.SetMaxConcurrentVoyagesPerVessel(request.MaxConcurrentVoyagesPerVessel.Value);
                    }

                    bool persisted = await scheduler.TryPersistAsync().ConfigureAwait(false);

                    ObjectiveSchedulerStatus status = BuildStatus(scheduler);
                    status.SettingsPersisted = persisted;
                    return (object)status;
                });

            if (coordination != null)
            {
                register(
                    "armada_objective_scheduler_clear_stale_pause",
                    "Clear a scheduler pause whose owning session has left. The autonomy layer's one permitted write to the pause: clear only, never engage. It succeeds only when the pause names its owner, the owner is absent from the coordination presence window, and the absence exceeds the configured threshold (settings autonomousObjectiveScheduler.stalePauseAbsenceMinutes, floor 30). Before clearing it wakes every active session and posts a board note naming the stale owner, the recorded reason, the set time and the measured absence, then it clears once and persists. A pause with no recorded owner is refused; an operator must clear it. The dispatch hold is never touched. Use dryRun to read the decision without acting.",
                    new
                    {
                        type = "object",
                        required = new[] { "clearedBy" },
                        properties = new
                        {
                            clearedBy = new { type = "string", description = "Your session participant key; it is written into the announcement." },
                            dryRun = new { type = "boolean", description = "When true, return the decision and evidence without clearing or announcing anything." }
                        }
                    },
                    async (args) =>
                    {
                        ClearStalePauseArgs? request = args.HasValue
                            ? JsonSerializer.Deserialize<ClearStalePauseArgs>(args.Value, _JsonOptions)
                            : null;
                        if (request == null || String.IsNullOrWhiteSpace(request.ClearedBy))
                            return (object)new { error = "clearedBy is required - name your session so the announcement says who cleared the pause." };

                        DateTime nowUtc = DateTime.UtcNow;
                        DateTime? ownerLastSeenUtc = null;
                        if (!String.IsNullOrWhiteSpace(scheduler.PausedBy))
                        {
                            List<CoordinationParticipant> participants = await coordination
                                .EnumerateParticipantsAsync(CoordinationService.DefaultRoomKey, activeWithinMinutes: 60 * 24 * 365)
                                .ConfigureAwait(false);
                            CoordinationParticipant? owner = participants.Find(x =>
                                String.Equals(x.ParticipantKey, scheduler.PausedBy, StringComparison.Ordinal));
                            if (owner != null) ownerLastSeenUtc = owner.LastSeenUtc;
                        }

                        int threshold = scheduler.StalePauseAbsenceMinutes;
                        StalePauseDecision decision = StalePauseRule.Evaluate(
                            scheduler.Paused, scheduler.PausedBy, scheduler.PausedUtc, ownerLastSeenUtc, nowUtc, threshold);

                        DateTime processStartUtc = System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime();
                        bool? admiralRestartedSincePause = scheduler.PausedUtc.HasValue ? processStartUtc > scheduler.PausedUtc.Value : (bool?)null;

                        object evidence = new
                        {
                            Paused = scheduler.Paused,
                            PausedBy = scheduler.PausedBy,
                            PausedUtc = scheduler.PausedUtc,
                            PauseReason = scheduler.PauseReason,
                            OwnerLastSeenUtc = ownerLastSeenUtc,
                            MeasuredAbsenceMinutes = decision.MeasuredAbsence.HasValue ? (int?)Math.Floor(decision.MeasuredAbsence.Value.TotalMinutes) : null,
                            ThresholdMinutes = threshold,
                            AdmiralStartUtc = processStartUtc,
                            AdmiralRestartedSincePause = admiralRestartedSincePause
                        };

                        if (!decision.CanClear || request.DryRun == true)
                        {
                            return (object)new { Cleared = false, CanClear = decision.CanClear, DryRun = request.DryRun == true, Reason = decision.Reason, Evidence = evidence };
                        }

                        string announcement = "[scheduler] Stale pause cleared by " + request.ClearedBy.Trim() + ". "
                            + decision.Reason + " The pause was set by " + scheduler.PausedBy + " at "
                            + scheduler.PausedUtc!.Value.ToString("yyyy-MM-ddTHH:mm:ssZ")
                            + (String.IsNullOrWhiteSpace(scheduler.PauseReason) ? " with no stated reason." : " for: " + scheduler.PauseReason + ".")
                            + (admiralRestartedSincePause == true
                                ? " The Admiral has restarted since the pause was set (started " + processStartUtc.ToString("yyyy-MM-ddTHH:mm:ssZ") + "), so a deploy it protected has already happened."
                                : " The Admiral has not restarted since the pause was set.")
                            + " Autonomous dispatch resumes on the next sweep. If this pause was yours and still needed, set it again with pausedBy and pauseReason.";

                        await coordination.EmitHoldWakeAsync(announcement).ConfigureAwait(false);
                        string? noteId = null;
                        try
                        {
                            CoordinationMessage note = await coordination.PostMessageAsync(
                                CoordinationService.DefaultRoomKey,
                                CoordinationAuthorTypeEnum.Operator,
                                request.ClearedBy.Trim(),
                                request.ClearedBy.Trim(),
                                announcement).ConfigureAwait(false);
                            noteId = note.Id;
                        }
                        catch (Exception ex)
                        {
                            return (object)new { Cleared = false, CanClear = true, Reason = "The board note could not be posted (" + ex.Message + "), so the pause was left in place: a silent clear is not acceptable.", Evidence = evidence };
                        }

                        scheduler.Resume();
                        bool persisted = await scheduler.TryPersistAsync().ConfigureAwait(false);
                        return (object)new { Cleared = true, CanClear = true, Reason = decision.Reason, Announcement = announcement, BoardNoteId = noteId, SettingsPersisted = persisted, Evidence = evidence };
                    });
            }

            register(
                "armada_mark_objective_auto_dispatchable",
                "Set the AutoDispatchEnabled flag and optionally update the blocker list for an objective.",
                new
                {
                    type = "object",
                    required = new[] { "objectiveId", "enabled" },
                    properties = new
                    {
                        objectiveId = new { type = "string", description = "ID of the objective to update." },
                        enabled = new { type = "boolean", description = "When true, the scheduler may auto-dispatch this objective." },
                        blockedByObjectiveIds = new { type = "array", items = new { type = "string" }, description = "Optional list of objective IDs that must reach Completed before this one may be dispatched. Omit to leave existing blockers unchanged." }
                    }
                },
                async (args) =>
                {
                    MarkAutoDispatchableArgs? request = args.HasValue
                        ? JsonSerializer.Deserialize<MarkAutoDispatchableArgs>(args.Value, _JsonOptions)
                        : null;

                    if (request == null || String.IsNullOrWhiteSpace(request.ObjectiveId))
                        return (object)new { error = "objectiveId is required." };

                    try
                    {
                        AuthContext auth = McpToolHelpers.CreateDefaultTenantAdminContext();
                        ObjectiveUpsertRequest upsert = new ObjectiveUpsertRequest
                        {
                            AutoDispatchEnabled = request.Enabled,
                            BlockedByObjectiveIds = request.BlockedByObjectiveIds
                        };

                        Objective updated = await objectiveService.UpdateAsync(auth, request.ObjectiveId, upsert).ConfigureAwait(false);

                        return (object)new
                        {
                            objectiveId = updated.Id,
                            title = updated.Title,
                            autoDispatchEnabled = updated.AutoDispatchEnabled,
                            blockedByObjectiveIds = updated.BlockedByObjectiveIds,
                            status = updated.Status.ToString()
                        };
                    }
                    catch (Exception ex)
                    {
                        return (object)new { error = ex.Message };
                    }
                });
        }

        /// <summary>
        /// Build an <see cref="ObjectiveSchedulerStatus"/> snapshot from the scheduler's current runtime state.
        /// </summary>
        /// <param name="scheduler">The scheduler to snapshot.</param>
        /// <returns>Populated status DTO.</returns>
        public static ObjectiveSchedulerStatus BuildStatus(AutonomousObjectiveScheduler scheduler)
        {
            if (scheduler == null) throw new ArgumentNullException(nameof(scheduler));

            return new ObjectiveSchedulerStatus
            {
                Enabled = scheduler.Enabled,
                Paused = scheduler.Paused,
                PausedBy = scheduler.PausedBy,
                PausedUtc = scheduler.PausedUtc,
                PauseReason = scheduler.PauseReason,
                IntervalMinutes = scheduler.IntervalMinutes,
                MaxConcurrentVoyages = scheduler.MaxConcurrentVoyages,
                MaxConcurrentVoyagesPerVessel = scheduler.MaxConcurrentVoyagesPerVessel,
                LastTickUtc = scheduler.LastTickUtc,
                ActiveDispatchedCount = scheduler.ActiveDispatchedCount,
                LastSkipReason = scheduler.LastSkipReason
            };
        }

        #endregion

        #region Private-Methods

        #endregion

        #region Private-Types

        /// <summary>
        /// Strongly-typed DTO for armada_objective_scheduler_set arguments.
        /// </summary>
        private sealed class SchedulerSetArgs
        {
            /// <summary>
            /// Optional enabled override.
            /// </summary>
            public bool? Enabled { get; set; }

            /// <summary>
            /// Optional paused override.
            /// </summary>
            public bool? Paused { get; set; }

            /// <summary>
            /// Participant key recorded on the pause.
            /// </summary>
            public string? PausedBy { get; set; }

            /// <summary>
            /// Reason recorded on the pause.
            /// </summary>
            public string? PauseReason { get; set; }

            /// <summary>
            /// Optional interval override in minutes.
            /// </summary>
            public int? IntervalMinutes { get; set; }

            /// <summary>
            /// Optional max concurrent voyages override.
            /// </summary>
            public int? MaxConcurrentVoyages { get; set; }

            /// <summary>
            /// Optional per-vessel concurrent voyage override.
            /// </summary>
            public int? MaxConcurrentVoyagesPerVessel { get; set; }
        }

        /// <summary>
        /// Strongly-typed DTO for armada_mark_objective_auto_dispatchable arguments.
        /// </summary>
        private sealed class MarkAutoDispatchableArgs
        {
            /// <summary>
            /// ID of the objective to update.
            /// </summary>
            public string ObjectiveId { get; set; } = string.Empty;

            /// <summary>
            /// Whether to opt this objective in to auto-dispatch.
            /// </summary>
            public bool Enabled { get; set; } = false;

            /// <summary>
            /// Optional blocker objective IDs. Null means leave unchanged.
            /// </summary>
            public List<string>? BlockedByObjectiveIds { get; set; } = null;
        }

        /// <summary>
        /// Strongly-typed DTO for armada_objective_scheduler_clear_stale_pause arguments.
        /// </summary>
        private sealed class ClearStalePauseArgs
        {
            /// <summary>
            /// Participant key of the clearing session.
            /// </summary>
            public string? ClearedBy { get; set; }

            /// <summary>
            /// When true, decide without acting.
            /// </summary>
            public bool? DryRun { get; set; }
        }

        #endregion
    }
}
