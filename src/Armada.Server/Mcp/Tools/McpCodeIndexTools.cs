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

            register(
                "armada_graph_search_symbols",
                "Search symbols in a vessel's code graph sidecars. Returns ranked symbol matches with kind, path, qualified name, and sidecar freshness warnings. Vectors are never included in results.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        vesselId = new { type = "string", description = "Vessel ID (vsl_ prefix)" },
                        query = new { type = "string", description = "Symbol name query (qualified or simple)" },
                        limit = new { type = "integer", description = "Maximum number of results (default 20)" },
                        pathPrefix = new { type = "string", description = "Optional repo-relative path prefix filter" },
                        kind = new { type = "string", description = "Optional symbol kind filter: Namespace, Class, Interface, Record, Enum, Struct, Method, Constructor, Property, Field, Delegate, Unknown" }
                    },
                    required = new[] { "vesselId", "query" }
                },
                async (args) =>
                {
                    if (!args.HasValue) return (object)new { Error = "missing args" };
                    CodeGraphSymbolSearchRequest request = JsonSerializer.Deserialize<CodeGraphSymbolSearchRequest>(args.Value, _JsonOptions)!;
                    if (String.IsNullOrWhiteSpace(request.VesselId)) return (object)new { Error = "vesselId is required" };
                    if (String.IsNullOrWhiteSpace(request.Query)) return (object)new { Error = "query is required" };
                    return (object)await codeIndex.SearchSymbolsAsync(request).ConfigureAwait(false);
                });

            register(
                "armada_graph_get_callers",
                "Resolve direct callers of a symbol from a vessel's code graph sidecars. Returns ranked neighbor results with edge kind, source path, and sidecar freshness warnings.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        vesselId = new { type = "string", description = "Vessel ID (vsl_ prefix)" },
                        symbol = new { type = "string", description = "Seed symbol name (qualified or simple)" },
                        limit = new { type = "integer", description = "Maximum number of caller results (default 25)" }
                    },
                    required = new[] { "vesselId", "symbol" }
                },
                async (args) =>
                {
                    if (!args.HasValue) return (object)new { Error = "missing args" };
                    CodeGraphNeighborsRequest request = JsonSerializer.Deserialize<CodeGraphNeighborsRequest>(args.Value, _JsonOptions)!;
                    if (String.IsNullOrWhiteSpace(request.VesselId)) return (object)new { Error = "vesselId is required" };
                    if (String.IsNullOrWhiteSpace(request.Symbol)) return (object)new { Error = "symbol is required" };
                    return (object)await codeIndex.GetCallersAsync(request).ConfigureAwait(false);
                });

            register(
                "armada_graph_get_callees",
                "Resolve direct callees of a symbol from a vessel's code graph sidecars. Returns ranked neighbor results with edge kind, source path, and sidecar freshness warnings.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        vesselId = new { type = "string", description = "Vessel ID (vsl_ prefix)" },
                        symbol = new { type = "string", description = "Seed symbol name (qualified or simple)" },
                        limit = new { type = "integer", description = "Maximum number of callee results (default 25)" }
                    },
                    required = new[] { "vesselId", "symbol" }
                },
                async (args) =>
                {
                    if (!args.HasValue) return (object)new { Error = "missing args" };
                    CodeGraphNeighborsRequest request = JsonSerializer.Deserialize<CodeGraphNeighborsRequest>(args.Value, _JsonOptions)!;
                    if (String.IsNullOrWhiteSpace(request.VesselId)) return (object)new { Error = "vesselId is required" };
                    if (String.IsNullOrWhiteSpace(request.Symbol)) return (object)new { Error = "symbol is required" };
                    return (object)await codeIndex.GetCalleesAsync(request).ConfigureAwait(false);
                });

            register(
                "armada_graph_get_impact",
                "Traverse graph relationships from a seed symbol using bounded depth. Returns impacted symbols with traversal depth, score, and sidecar freshness warnings. Direction can be Callers, Callees, or Both.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        vesselId = new { type = "string", description = "Vessel ID (vsl_ prefix)" },
                        symbol = new { type = "string", description = "Seed symbol name (qualified or simple)" },
                        direction = new { type = "string", description = "Traversal direction: Callers, Callees, or Both (default Both)" },
                        maxDepth = new { type = "integer", description = "Maximum traversal depth (default 3)" },
                        maxResults = new { type = "integer", description = "Maximum number of impacted symbols to return (default 50)" }
                    },
                    required = new[] { "vesselId", "symbol" }
                },
                async (args) =>
                {
                    if (!args.HasValue) return (object)new { Error = "missing args" };
                    CodeGraphImpactRequest request = JsonSerializer.Deserialize<CodeGraphImpactRequest>(args.Value, _JsonOptions)!;
                    if (String.IsNullOrWhiteSpace(request.VesselId)) return (object)new { Error = "vesselId is required" };
                    if (String.IsNullOrWhiteSpace(request.Symbol)) return (object)new { Error = "symbol is required" };
                    return (object)await codeIndex.GetImpactAsync(request).ConfigureAwait(false);
                });

            register(
                "armada_graph_suggest_affected_tests",
                "Suggest test files likely affected by a symbol change using graph traversal and path-convention fallback. Returns ranked candidates with evidence depth, reasons, and sidecar freshness warnings.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        vesselId = new { type = "string", description = "Vessel ID (vsl_ prefix)" },
                        symbol = new { type = "string", description = "Seed symbol name (qualified or simple)" },
                        maxDepth = new { type = "integer", description = "Maximum traversal depth used to collect evidence (default 3)" },
                        maxResults = new { type = "integer", description = "Maximum number of suggested test candidates (default 20)" }
                    },
                    required = new[] { "vesselId", "symbol" }
                },
                async (args) =>
                {
                    if (!args.HasValue) return (object)new { Error = "missing args" };
                    CodeGraphAffectedTestsRequest request = JsonSerializer.Deserialize<CodeGraphAffectedTestsRequest>(args.Value, _JsonOptions)!;
                    if (String.IsNullOrWhiteSpace(request.VesselId)) return (object)new { Error = "vesselId is required" };
                    if (String.IsNullOrWhiteSpace(request.Symbol)) return (object)new { Error = "symbol is required" };
                    return (object)await codeIndex.SuggestAffectedTestsAsync(request).ConfigureAwait(false);
                });

            register(
                "armada_graph_get_node",
                "Resolve one graph symbol with direct callers, callees, and optional source excerpt.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        vesselId = new { type = "string", description = "Vessel ID (vsl_ prefix)" },
                        symbol = new { type = "string", description = "Seed symbol name (qualified or simple)" },
                        includeSource = new { type = "boolean", description = "Include source excerpt (default true)" },
                        sourcePadding = new { type = "integer", description = "Additional lines around the symbol source range (default 2)" }
                    },
                    required = new[] { "vesselId", "symbol" }
                },
                async (args) =>
                {
                    if (!args.HasValue) return (object)new { Error = "missing args" };
                    CodeGraphNodeRequest request = JsonSerializer.Deserialize<CodeGraphNodeRequest>(args.Value, _JsonOptions)!;
                    if (String.IsNullOrWhiteSpace(request.VesselId)) return (object)new { Error = "vesselId is required" };
                    if (String.IsNullOrWhiteSpace(request.Symbol)) return (object)new { Error = "symbol is required" };
                    return (object)await codeIndex.GetNodeAsync(request).ConfigureAwait(false);
                });

            register(
                "armada_graph_get_files",
                "Return indexed file structure from graph sidecars, optionally including symbols per file.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        vesselId = new { type = "string", description = "Vessel ID (vsl_ prefix)" },
                        pathPrefix = new { type = "string", description = "Optional repo-relative path prefix filter" },
                        limit = new { type = "integer", description = "Maximum files to return (default 100)" },
                        includeSymbols = new { type = "boolean", description = "Include symbols per file (default true)" }
                    },
                    required = new[] { "vesselId" }
                },
                async (args) =>
                {
                    if (!args.HasValue) return (object)new { Error = "missing args" };
                    CodeGraphFileStructureRequest request = JsonSerializer.Deserialize<CodeGraphFileStructureRequest>(args.Value, _JsonOptions)!;
                    if (String.IsNullOrWhiteSpace(request.VesselId)) return (object)new { Error = "vesselId is required" };
                    return (object)await codeIndex.GetFileStructureAsync(request).ConfigureAwait(false);
                });

            register(
                "armada_graph_explore",
                "Explore graph relationships and source excerpts around a symbol query. Returns grouped files and relationship edges.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        vesselId = new { type = "string", description = "Vessel ID (vsl_ prefix)" },
                        query = new { type = "string", description = "Symbol or conceptual symbol query" },
                        maxDepth = new { type = "integer", description = "Maximum graph traversal depth (default 2)" },
                        maxResults = new { type = "integer", description = "Maximum symbols to include (default 25)" },
                        includeSource = new { type = "boolean", description = "Include source excerpts grouped by file (default true)" }
                    },
                    required = new[] { "vesselId", "query" }
                },
                async (args) =>
                {
                    if (!args.HasValue) return (object)new { Error = "missing args" };
                    CodeGraphExploreRequest request = JsonSerializer.Deserialize<CodeGraphExploreRequest>(args.Value, _JsonOptions)!;
                    if (String.IsNullOrWhiteSpace(request.VesselId)) return (object)new { Error = "vesselId is required" };
                    if (String.IsNullOrWhiteSpace(request.Query)) return (object)new { Error = "query is required" };
                    return (object)await codeIndex.ExploreAsync(request).ConfigureAwait(false);
                });
        }
    }
}
