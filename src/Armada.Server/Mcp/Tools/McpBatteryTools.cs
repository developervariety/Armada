namespace Armada.Server.Mcp.Tools
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Models;
    using Armada.Core.Services;

    /// <summary>
    /// Registers the read-only fixture battery tools: list packs, list runs and read one run. They are operator
    /// tools only: they are not caller-scoped, so a mission caller can neither list nor call them, and each handler
    /// refuses any caller other than a global administrator. Starting a run spends a captain's tokens, so it is a REST
    /// action only (<c>POST /api/v1/battery/runs</c>) and has no tool.
    /// </summary>
    public static class McpBatteryTools
    {
        #region Public-Members

        /// <summary>Registered name of the pack list tool.</summary>
        public const string PacksToolName = "armada_battery_packs";

        /// <summary>Registered name of the run list tool.</summary>
        public const string RunsToolName = "armada_battery_runs";

        /// <summary>Registered name of the run read tool.</summary>
        public const string RunToolName = "armada_battery_run";

        /// <summary>Refusal reason returned to a caller other than a global administrator.</summary>
        public const string GlobalAdministratorRequiredReason = "global_administrator_required";

        #endregion

        #region Private-Members

        private static readonly JsonSerializerOptions _JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        #endregion

        #region Public-Methods

        /// <summary>
        /// Register the battery tools.
        /// </summary>
        /// <param name="register">Tool registration delegate.</param>
        /// <param name="service">Battery run service.</param>
        /// <param name="database">Database driver.</param>
        public static void Register(RegisterToolDelegate register, BatteryRunService service, DatabaseDriver database)
        {
            if (register == null) throw new ArgumentNullException(nameof(register));
            if (service == null) throw new ArgumentNullException(nameof(service));
            if (database == null) throw new ArgumentNullException(nameof(database));

            register(
                PacksToolName,
                "List the fixture battery packs: each pack's version, toolchain and tasks with their grader types. A malformed pack is listed with valid false and its validation errors. Read-only.",
                new { type = "object", properties = new { }, required = new string[] { } },
                (args) =>
                {
                    if (!McpCallerContext.Require().IsAdmin) return Task.FromResult(Refused());
                    return Task.FromResult((object)service.ListPacks());
                });

            register(
                RunsToolName,
                "List fixture battery runs, newest first. Each run names the captain, the pack and its version, and the grade of each task reached (Passed, Failed, or CouldNotRun when the task could not be judged). Read-only; start a run with POST /api/v1/battery/runs.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        captainId = new { type = "string", description = "Only runs of this captain (cpt_ prefix)" },
                        packId = new { type = "string", description = "Only runs of this pack" },
                        limit = new { type = "integer", description = "Most runs to return (default 50, maximum 200)" }
                    },
                    required = new string[] { }
                },
                async (args) =>
                {
                    if (!McpCallerContext.Require().IsAdmin) return Refused();
                    ListArgs request = Read<ListArgs>(args);
                    return (object)await database.BatteryRuns.EnumerateAsync(request.CaptainId, request.PackId, request.Limit ?? 50, McpCallerContext.CallToken).ConfigureAwait(false);
                });

            register(
                RunToolName,
                "Read one fixture battery run with the grade, timings and evidence of each task, and the mission and voyage that ran it. The attempts behind a grade are the mission's attempt provenance (armada_mission_attempts). Read-only.",
                new
                {
                    type = "object",
                    properties = new { runId = new { type = "string", description = "Battery run id (bat_ prefix)" } },
                    required = new[] { "runId" }
                },
                async (args) =>
                {
                    if (!McpCallerContext.Require().IsAdmin) return Refused();
                    RunArgs request = Read<RunArgs>(args);
                    if (String.IsNullOrWhiteSpace(request.RunId)) return (object)new { Error = "runId is required." };
                    BatteryRun? run = await database.BatteryRuns.ReadAsync(request.RunId.Trim(), McpCallerContext.CallToken).ConfigureAwait(false);
                    if (run == null) return (object)new { Error = "Battery run not found" };
                    return (object)run;
                });
        }

        #endregion

        #region Private-Methods

        private static object Refused()
        {
            return new { Error = "Battery tools require a global administrator.", Reason = GlobalAdministratorRequiredReason };
        }

        private static T Read<T>(JsonElement? args) where T : new()
        {
            if (!args.HasValue || args.Value.ValueKind != JsonValueKind.Object) return new T();
            return JsonSerializer.Deserialize<T>(args.Value, _JsonOptions) ?? new T();
        }

        #endregion

        #region Private-Types

        private sealed class ListArgs
        {
            public string? CaptainId { get; set; } = null;

            public string? PackId { get; set; } = null;

            public int? Limit { get; set; } = null;
        }

        private sealed class RunArgs
        {
            public string? RunId { get; set; } = null;
        }

        #endregion
    }
}
