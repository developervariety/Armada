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

        /// <summary>Event type recorded when the scheduler becomes paused.</summary>
        public const string PausedEventType = "objective_scheduler.paused";

        /// <summary>Event type recorded when the scheduler resumes from a pause.</summary>
        public const string ResumedEventType = "objective_scheduler.resumed";

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
        /// <param name="scheduler">Optional autonomous objective scheduler instance.</param>
        /// <param name="database">Database driver for objective persistence.</param>
        /// <param name="objectiveService">Objective service for updating objectives.</param>
        public static void Register(
            RegisterToolDelegate register,
            AutonomousObjectiveScheduler? scheduler,
            DatabaseDriver database,
            ObjectiveService objectiveService)
        {
            if (register == null) throw new ArgumentNullException(nameof(register));
            if (database == null) throw new ArgumentNullException(nameof(database));
            if (objectiveService == null) throw new ArgumentNullException(nameof(objectiveService));

            register(
                "armada_objective_scheduler_status",
                "Get the current runtime status of the autonomous objective scheduler.",
                new { type = "object", properties = new { } },
                async (args) =>
                {
                    if (scheduler == null)
                        return McpToolUnavailable.Create("objective scheduler");
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
                        paused = new { type = "boolean", description = "When true the scheduler is suspended without clearing the enabled flag. A change of the paused state records an objective_scheduler.paused or objective_scheduler.resumed event naming the authenticated caller and the reason." },
                        reason = new { type = "string", description = "Why the paused state changes, for example the deploy the pause protects. Recorded on the event and, for a pause, on the scheduler." },
                        pauseReason = new { type = "string", description = "Alias of reason for a pause." },
                        intervalMinutes = new { type = "integer", description = "Sweep interval in minutes (clamped to 1-1440)." },
                        maxConcurrentVoyages = new { type = "integer", description = "Fleet-wide active objective voyage ceiling (clamped to 1-50). Operator-dispatched linked voyages count toward it." },
                        maxConcurrentVoyagesPerVessel = new { type = "integer", description = "Per-vessel active objective voyage ceiling (clamped to 1-50, default 1). This prevents one vessel from consuming the fleet-wide capacity." },
                        fairShareWithinPriorityBands = new { type = "boolean", description = "When true, rotate campaign roots within each priority band. Disabled by default; priority bands remain authoritative." }
                    }
                },
                async (args) =>
                {
                    SchedulerSetArgs? request = args.HasValue
                        ? JsonSerializer.Deserialize<SchedulerSetArgs>(args.Value, _JsonOptions)
                        : null;

                    if (scheduler == null)
                        return McpToolUnavailable.Create("objective scheduler");

                    if (request != null)
                    {
                        if (request.Enabled.HasValue)
                        {
                            if (request.Enabled.Value) scheduler.Enable();
                            else scheduler.Disable();
                        }

                        if (request.Paused.HasValue)
                        {
                            bool wasPaused = scheduler.Paused;
                            string? caller = McpCallerContext.CallerIdentity;
                            string? reason = String.IsNullOrWhiteSpace(request.Reason) ? request.PauseReason : request.Reason;
                            if (request.Paused.Value) scheduler.Pause(caller, reason);
                            else scheduler.Resume();
                            if (wasPaused != request.Paused.Value)
                                await RecordPauseChangeAsync(database, request.Paused.Value, caller, reason).ConfigureAwait(false);
                        }

                        if (request.IntervalMinutes.HasValue)
                            scheduler.SetIntervalMinutes(request.IntervalMinutes.Value);

                        if (request.MaxConcurrentVoyages.HasValue)
                            scheduler.SetMaxConcurrentVoyages(request.MaxConcurrentVoyages.Value);

                        if (request.MaxConcurrentVoyagesPerVessel.HasValue)
                            scheduler.SetMaxConcurrentVoyagesPerVessel(request.MaxConcurrentVoyagesPerVessel.Value);

                        if (request.FairShareWithinPriorityBands.HasValue)
                            scheduler.SetFairShareWithinPriorityBands(request.FairShareWithinPriorityBands.Value);
                    }

                    bool persisted = await scheduler.TryPersistAsync().ConfigureAwait(false);

                    ObjectiveSchedulerStatus status = BuildStatus(scheduler);
                    status.SettingsPersisted = persisted;
                    return (object)status;
                });

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
                    MarkAutoDispatchableArgs? request;
                    try
                    {
                        request = args.HasValue
                            ? JsonSerializer.Deserialize<MarkAutoDispatchableArgs>(args.Value, _JsonOptions)
                            : null;
                    }
                    catch (JsonException)
                    {
                        // The argument DTO disallows unmapped members, so an unknown field (for
                        // example a misnamed enabled flag) is rejected here rather than ignored.
                        // Ignoring it would silently drop the caller's intent and apply the default.
                        return (object)new
                        {
                            error = "armada_mark_objective_auto_dispatchable received an unexpected field.",
                            code = "unexpected_field",
                            reason = "The arguments carried a field the tool does not accept.",
                            action = "Pass only objectiveId, enabled, and optionally blockedByObjectiveIds. The opt-in flag is named enabled."
                        };
                    }

                    if (request == null || String.IsNullOrWhiteSpace(request.ObjectiveId))
                        return (object)new { error = "objectiveId is required." };

                    // enabled is required. Without this guard an omitted flag deserializes to its
                    // default and silently opts the objective OUT while returning success, so a
                    // caller who meant to opt in cannot tell the call did the opposite.
                    if (!request.Enabled.HasValue)
                        return (object)new
                        {
                            error = "armada_mark_objective_auto_dispatchable requires the enabled flag.",
                            code = "missing_enabled",
                            reason = "The enabled flag was absent.",
                            action = "Pass enabled=true to opt the objective in to auto-dispatch, or enabled=false to opt it out."
                        };

                    try
                    {
                        AuthContext auth = McpCallerContext.Require();
                        ObjectiveUpsertRequest upsert = new ObjectiveUpsertRequest
                        {
                            AutoDispatchEnabled = request.Enabled.Value,
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
        /// Record that the scheduler's paused state changed, naming the authenticated caller and the stated reason.
        /// </summary>
        private static Task RecordPauseChangeAsync(DatabaseDriver database, bool paused, string? caller, string? reason)
        {
            string who = String.IsNullOrWhiteSpace(caller) ? "unknown" : caller!;
            string eventType = paused ? PausedEventType : ResumedEventType;
            ArmadaEvent evt = new ArmadaEvent(eventType,
                "Objective scheduler " + (paused ? "paused" : "resumed") + " by " + who
                + (String.IsNullOrWhiteSpace(reason) ? "." : ": " + reason!.Trim()))
            {
                EntityType = "system",
                Payload = JsonSerializer.Serialize(new { Caller = caller, Reason = String.IsNullOrWhiteSpace(reason) ? null : reason!.Trim() })
            };
            return database.Events.CreateAsync(evt);
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
                FairShareWithinPriorityBands = scheduler.FairShareWithinPriorityBands,
                LastServedCampaignByPriority = scheduler.LastServedCampaignByPriority.ToDictionary(
                    pair => pair.Key.ToString(),
                    pair => pair.Value,
                    StringComparer.Ordinal),
                LastTickUtc = scheduler.LastTickUtc,
                ActiveDispatchedCount = scheduler.ActiveDispatchedCount,
                EventTriggeredSweepCount = scheduler.EventTriggeredSweepCount,
                LastSkipReason = scheduler.LastSkipReason,
                LastSweepSkippedObjectives = scheduler.LastSweepSkippedObjectives.ToList(),
                LastSweepSkippedObjectiveCount = scheduler.LastSweepSkippedObjectiveCount,
                SweepInProgress = scheduler.SweepInProgress,
                LastSweepStartedUtc = scheduler.LastSweepStartedUtc,
                LastSweepCompletedUtc = scheduler.LastSweepCompletedUtc,
                SweepCandidateCount = scheduler.SweepCandidateCount,
                SweepCandidatesExamined = scheduler.SweepCandidatesExamined,
                SweepDispatchedCount = scheduler.SweepDispatchedCount,
                LastSweepBoundReached = scheduler.LastSweepBoundReached,
                LastSweepError = scheduler.LastSweepError
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
            /// Reason recorded on the pause or resume.
            /// </summary>
            public string? Reason { get; set; }

            /// <summary>
            /// Alias of <see cref="Reason"/> for a pause.
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

            /// <summary>
            /// Optional campaign fair-share override.
            /// </summary>
            public bool? FairShareWithinPriorityBands { get; set; }
        }

        /// <summary>
        /// Strongly-typed DTO for armada_mark_objective_auto_dispatchable arguments.
        /// Unmapped members are disallowed so a misnamed field is reported, not ignored.
        /// </summary>
        [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
        private sealed class MarkAutoDispatchableArgs
        {
            /// <summary>
            /// ID of the objective to update.
            /// </summary>
            public string ObjectiveId { get; set; } = string.Empty;

            /// <summary>
            /// Whether to opt this objective in to auto-dispatch. Null means the caller omitted
            /// the flag, which the tool rejects rather than applying a default opt-out.
            /// </summary>
            public bool? Enabled { get; set; } = null;

            /// <summary>
            /// Optional blocker objective IDs. Null means leave unchanged.
            /// </summary>
            public List<string>? BlockedByObjectiveIds { get; set; } = null;
        }

        #endregion
    }
}
