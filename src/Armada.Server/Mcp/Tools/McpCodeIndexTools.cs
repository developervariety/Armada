namespace Armada.Server.Mcp.Tools
{
    using System;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;

    /// <summary>
    /// Registers MCP tools for Admiral-owned code indexing and context-pack generation.
    /// </summary>
    public static class McpCodeIndexTools
    {
        private static readonly JsonSerializerOptions _JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        /// <summary>
        /// Register code index MCP tools.
        /// </summary>
        /// <param name="register">Tool registration delegate.</param>
        /// <param name="codeIndex">Code index service.</param>
        public static void Register(RegisterToolDelegate register, ICodeIndexService codeIndex)
        {
            if (register == null) throw new ArgumentNullException(nameof(register));
            if (codeIndex == null) throw new ArgumentNullException(nameof(codeIndex));

            register(
                "armada_index_status",
                "Get code index status for a vessel, including indexed commit, current commit, chunk counts, and freshness.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        vesselId = new { type = "string", description = "Vessel ID (vsl_ prefix)" }
                    },
                    required = new[] { "vesselId" }
                },
                async (args) =>
                {
                    if (!args.HasValue) return (object)new { Error = "missing args" };
                    VesselIdArgs request = JsonSerializer.Deserialize<VesselIdArgs>(args.Value, _JsonOptions)!;
                    if (String.IsNullOrWhiteSpace(request.VesselId)) return (object)new { Error = "vesselId is required" };
                    return (object)await codeIndex.GetStatusAsync(request.VesselId).ConfigureAwait(false);
                });

            register(
                "armada_index_update",
                "Refresh the Admiral-owned code index for a vessel's default branch.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        vesselId = new { type = "string", description = "Vessel ID (vsl_ prefix)" }
                    },
                    required = new[] { "vesselId" }
                },
                async (args) =>
                {
                    if (!args.HasValue) return (object)new { Error = "missing args" };
                    VesselIdArgs request = JsonSerializer.Deserialize<VesselIdArgs>(args.Value, _JsonOptions)!;
                    if (String.IsNullOrWhiteSpace(request.VesselId)) return (object)new { Error = "vesselId is required" };
                    return (object)await codeIndex.UpdateAsync(request.VesselId).ConfigureAwait(false);
                });

            register(
                "armada_code_search",
                "Search a vessel's Admiral-owned code index. Results include vesselId, repo-relative path, commit SHA, content hash, language, line range, and freshness.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        vesselId = new { type = "string", description = "Vessel ID (vsl_ prefix)" },
                        query = new { type = "string", description = "Search query" },
                        limit = new { type = "integer", description = "Maximum results (default 10)" },
                        pathPrefix = new { type = "string", description = "Optional repo-relative path prefix filter" },
                        language = new { type = "string", description = "Optional language filter, e.g. csharp or markdown" },
                        includeContent = new { type = "boolean", description = "Include full chunk content in results" },
                        includeReferenceOnly = new { type = "boolean", description = "Include records marked reference-only" }
                    },
                    required = new[] { "vesselId", "query" }
                },
                async (args) =>
                {
                    if (!args.HasValue) return (object)new { Error = "missing args" };
                    CodeSearchRequest request = JsonSerializer.Deserialize<CodeSearchRequest>(args.Value, _JsonOptions)!;
                    if (String.IsNullOrWhiteSpace(request.VesselId)) return (object)new { Error = "vesselId is required" };
                    if (String.IsNullOrWhiteSpace(request.Query)) return (object)new { Error = "query is required" };
                    return (object)await codeIndex.SearchAsync(request).ConfigureAwait(false);
                });

            register(
                "armada_context_pack",
                "Build dispatch-ready code context for a vessel and mission goal. Returns markdown plus a prestagedFiles entry for _briefing/context-pack.md.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        vesselId = new { type = "string", description = "Vessel ID (vsl_ prefix)" },
                        goal = new { type = "string", description = "Mission goal or implementation objective" },
                        tokenBudget = new { type = "integer", description = "Approximate markdown token budget" },
                        maxResults = new { type = "integer", description = "Optional maximum evidence snippets" }
                    },
                    required = new[] { "vesselId", "goal", "tokenBudget" }
                },
                async (args) =>
                {
                    if (!args.HasValue) return (object)new { Error = "missing args" };
                    ContextPackRequest request = JsonSerializer.Deserialize<ContextPackRequest>(args.Value, _JsonOptions)!;
                    if (String.IsNullOrWhiteSpace(request.VesselId)) return (object)new { Error = "vesselId is required" };
                    if (String.IsNullOrWhiteSpace(request.Goal)) return (object)new { Error = "goal is required" };
                    return (object)await codeIndex.BuildContextPackAsync(request).ConfigureAwait(false);
                });

            register(
                "armada_fleet_code_search",
                "Search all vessels in a fleet in one call. Results are merged, re-ranked by score, and include vessel attribution.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        fleetId = new { type = "string", description = "Fleet ID (flt_ prefix)" },
                        query = new { type = "string", description = "Search query" },
                        limit = new { type = "integer", description = "Maximum results (default per-vessel default x vessel count, capped at 50)" },
                        pathPrefix = new { type = "string", description = "Optional repo-relative path prefix filter" },
                        language = new { type = "string", description = "Optional language filter, e.g. csharp or markdown" },
                        includeContent = new { type = "boolean", description = "Include full chunk content in results" },
                        includeReferenceOnly = new { type = "boolean", description = "Include records marked reference-only" }
                    },
                    required = new[] { "fleetId", "query" }
                },
                async (args) =>
                {
                    if (!args.HasValue) return (object)new { Error = "missing args" };
                    FleetCodeSearchRequest request = JsonSerializer.Deserialize<FleetCodeSearchRequest>(args.Value, _JsonOptions)!;
                    if (String.IsNullOrWhiteSpace(request.FleetId)) return (object)new { Error = "fleetId is required" };
                    if (String.IsNullOrWhiteSpace(request.Query)) return (object)new { Error = "query is required" };
                    return (object)await codeIndex.SearchFleetAsync(request).ConfigureAwait(false);
                });

            register(
                "armada_fleet_context_pack",
                "Build a dispatch-ready context pack across all vessels in a fleet. Returns markdown plus a prestagedFiles entry for _briefing/context-pack.md.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        fleetId = new { type = "string", description = "Fleet ID (flt_ prefix)" },
                        goal = new { type = "string", description = "Mission goal or implementation objective" },
                        tokenBudget = new { type = "integer", description = "Approximate markdown token budget" },
                        maxResultsPerVessel = new { type = "integer", description = "Optional maximum evidence snippets per vessel" }
                    },
                    required = new[] { "fleetId", "goal", "tokenBudget" }
                },
                async (args) =>
                {
                    if (!args.HasValue) return (object)new { Error = "missing args" };
                    FleetContextPackRequest request = JsonSerializer.Deserialize<FleetContextPackRequest>(args.Value, _JsonOptions)!;
                    if (String.IsNullOrWhiteSpace(request.FleetId)) return (object)new { Error = "fleetId is required" };
                    if (String.IsNullOrWhiteSpace(request.Goal)) return (object)new { Error = "goal is required" };
                    return (object)await codeIndex.BuildFleetContextPackAsync(request).ConfigureAwait(false);
                });
        }
    }
}
