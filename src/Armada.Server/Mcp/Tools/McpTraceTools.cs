namespace Armada.Server.Mcp.Tools
{
    using System;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services;

    /// <summary>
    /// Registers the MCP tools that read the trace table and fill it from history.
    /// </summary>
    public static class McpTraceTools
    {
        #region Public-Members

        /// <summary>
        /// Refusal reason returned when a caller other than a global administrator calls a trace tool.
        /// </summary>
        public const string GlobalAdministratorRequiredReason = "global_administrator_required";

        /// <summary>
        /// Refusal message returned when a caller other than a global administrator calls a trace tool.
        /// </summary>
        public const string GlobalAdministratorRequiredMessage = "Trace records span every tenant; reading or filling them requires a global administrator.";

        #endregion

        #region Private-Members

        private static readonly JsonSerializerOptions _JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        private const int _DefaultBackfillRows = 2000;
        private const int _MaximumBackfillRows = 20000;
        private static readonly TimeSpan _DefaultBackfillLookBack = TimeSpan.FromDays(120);

        #endregion

        #region Public-Methods

        /// <summary>
        /// Register the trace tools.
        /// </summary>
        /// <param name="register">Tool registration delegate.</param>
        /// <param name="recorder">Trace recorder.</param>
        public static void Register(RegisterToolDelegate register, TraceRecorder recorder)
        {
            if (register == null) throw new ArgumentNullException(nameof(register));
            if (recorder == null) throw new ArgumentNullException(nameof(recorder));

            register(
                "armada_trace",
                "Return the linked chain around one id from the trace table: the objective, voyage, missions, Judge and rescue missions, Challenger, check runs, merge entries, incidents and deployments it connects to, each as a compact record (status timeline, vessel, captain, model, persona, branch, start ref, commit, landed commit, deployed artifact, gate result with failing test names, check totals, Judge verdict and finding count, failure reason, archive pointers). Takes a full id (msn_, vyg_, obj_, chk_, inc_, mrg_, dpl_) or a short id: a prefix of a full id, with or without its kind prefix. A record missing from the table is built from its live source row. Trace records are kept for archive.traceRetentionDays after their record last changed, so the chain outlives the raw rows. Global administrators only.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        id = new { type = "string", description = "Full or short id of an objective, voyage, mission, check run, incident, merge entry or deployment" },
                        maxRecords = new { type = "integer", description = "Most records returned (default " + TraceRecorder.DefaultMaximumChainRecords + ", maximum " + TraceRecorder.MaximumChainRecords + ")" },
                        includeMessages = new { type = "boolean", description = "Return the final message of every record, not only the requested one (default false)" }
                    },
                    required = new[] { "id" }
                },
                async (args) =>
                {
                    if (!McpCallerContext.Require().IsAdmin)
                        return (object)new { Error = GlobalAdministratorRequiredMessage, Reason = GlobalAdministratorRequiredReason };
                    if (!args.HasValue) return (object)new { Error = "missing args" };
                    TraceArgs request = JsonSerializer.Deserialize<TraceArgs>(args.Value, _JsonOptions) ?? new TraceArgs();
                    if (String.IsNullOrWhiteSpace(request.Id)) return (object)new { Error = "id is required" };

                    TraceChain chain = await recorder.TraceAsync(request.Id, request.MaxRecords ?? TraceRecorder.DefaultMaximumChainRecords).ConfigureAwait(false);
                    if (!request.IncludeMessages)
                    {
                        for (int index = 1; index < chain.Records.Count; index++) chain.Records[index].FinalMessage = null;
                    }
                    if (chain.ResolvedId == null)
                    {
                        string error = chain.Candidates.Count > 0
                            ? "id matches more than one record; give more of the id"
                            : "no trace record and no source record matches the id";
                        return (object)new { Error = error, chain.RequestedId, chain.Candidates };
                    }
                    return (object)chain;
                });

            register(
                "armada_trace_backfill",
                "Fill the trace table from stored history, one bounded batch per call. Walks the objective, voyage, mission, merge entry, deployment and incident tables in creation order from sinceUtc (only on the first call; later calls resume from each table's cursor), records every row with the check runs, merge entries and deployments of each mission and voyage, and returns counts. Call again until done is true. Every write replaces the stored record of the same id, so a repeated batch is safe. Global administrators only.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        sinceUtc = new { type = "string", description = "Oldest creation time (ISO 8601 UTC) for the first call (default 120 days ago)" },
                        maxRows = new { type = "integer", description = "Most source rows one call visits (default " + _DefaultBackfillRows + ", maximum " + _MaximumBackfillRows + ")" }
                    },
                    required = new string[] { }
                },
                async (args) =>
                {
                    if (!McpCallerContext.Require().IsAdmin)
                        return (object)new { Error = GlobalAdministratorRequiredMessage, Reason = GlobalAdministratorRequiredReason };
                    TraceBackfillArgs request = args.HasValue
                        ? JsonSerializer.Deserialize<TraceBackfillArgs>(args.Value, _JsonOptions) ?? new TraceBackfillArgs()
                        : new TraceBackfillArgs();
                    DateTime since = request.SinceUtc.HasValue
                        ? request.SinceUtc.Value.ToUniversalTime()
                        : DateTime.UtcNow - _DefaultBackfillLookBack;
                    int maxRows = Math.Clamp(request.MaxRows ?? _DefaultBackfillRows, 1, _MaximumBackfillRows);
                    TraceBackfillResult result = await recorder.BackfillAsync(since, maxRows).ConfigureAwait(false);
                    return (object)result;
                });
        }

        #endregion
    }
}
