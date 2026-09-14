namespace Armada.Server.Mcp.Tools
{
    using System;
    using System.Text.Json;
    using Armada.Core.Database;
    using Armada.Core.Models;
    using Armada.Core.Services;

    /// <summary>
    /// Registers the read-only verified production summary tool.
    /// </summary>
    public static class McpProductionTools
    {
        private static readonly JsonSerializerOptions _JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        /// <summary>
        /// Register production reporting tools.
        /// </summary>
        /// <param name="register">Tool registration delegate.</param>
        /// <param name="database">Database used by the summary service.</param>
        public static void Register(RegisterToolDelegate register, DatabaseDriver database)
        {
            VerifiedProductionSummaryService production = new VerifiedProductionSummaryService(database);

            register(
                "armada_production_summary",
                "Return an evidence-based production baseline for a UTC window. The result reports verified landed slices, dispatch delay, Check armed-to-start and execution time, first-pass acceptance, rescue cost, closeout delay, post-land regressions, repeated research, eligible idle lane time, and explicit warnings when evidence is incomplete or unavailable. Host-slot queue time stays unavailable until its start timestamp is durable. Use sourceFamily and workType to compare like work with like work. Do not infer an improvement percentage from this result until a baseline window exists.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        fromUtc = new { type = "string", description = "UTC summary start timestamp in ISO-8601 form" },
                        toUtc = new { type = "string", description = "UTC summary end timestamp in ISO-8601 form" },
                        sourceFamily = new { type = "string", description = "Optional exact objective source-family filter" },
                        workType = new { type = "string", description = "Optional exact objective work-type filter" }
                    },
                    required = Array.Empty<string>()
                },
                async (args) =>
                {
                    ProductionSummaryQuery query = args == null
                        ? new ProductionSummaryQuery()
                        : JsonSerializer.Deserialize<ProductionSummaryQuery>(args.Value, _JsonOptions)
                            ?? new ProductionSummaryQuery();
                    ProductionSummaryResult result = await production.SummarizeAsync(
                        McpCallerContext.Require(),
                        query).ConfigureAwait(false);
                    return (object)result;
                });
        }
    }
}
