namespace Armada.Server.Mcp.Tools
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Core.Database;
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
        public static void Register(
            RegisterToolDelegate register,
            AutonomousObjectiveScheduler scheduler,
            DatabaseDriver database,
            ObjectiveService objectiveService)
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
                        paused = new { type = "boolean", description = "When true the scheduler is suspended without clearing the enabled flag." },
                        intervalMinutes = new { type = "integer", description = "Sweep interval in minutes (clamped to 1-1440)." },
                        maxConcurrentVoyages = new { type = "integer", description = "Maximum simultaneously active scheduler-dispatched voyages (clamped to 1-50)." }
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
                            if (request.Paused.Value) scheduler.Pause();
                            else scheduler.Resume();
                        }

                        if (request.IntervalMinutes.HasValue)
                            scheduler.SetIntervalMinutes(request.IntervalMinutes.Value);

                        if (request.MaxConcurrentVoyages.HasValue)
                            scheduler.SetMaxConcurrentVoyages(request.MaxConcurrentVoyages.Value);
                    }

                    await Task.CompletedTask.ConfigureAwait(false);
                    return (object)BuildStatus(scheduler);
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
                IntervalMinutes = scheduler.IntervalMinutes,
                MaxConcurrentVoyages = scheduler.MaxConcurrentVoyages,
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
            /// Optional interval override in minutes.
            /// </summary>
            public int? IntervalMinutes { get; set; }

            /// <summary>
            /// Optional max concurrent voyages override.
            /// </summary>
            public int? MaxConcurrentVoyages { get; set; }
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

        #endregion
    }
}
