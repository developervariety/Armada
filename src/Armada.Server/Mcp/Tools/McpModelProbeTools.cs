namespace Armada.Server.Mcp.Tools
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Server.Routes;

    /// <summary>
    /// Registers the Evaluate MCP tools: evaluate_captain (a Quick probe-only Evaluate of a captain or a model endpoint),
    /// get_model_probe_run and list_model_probe_runs. Every tool enforces the same rules as REST through
    /// <see cref="ModelProbeAccessService"/>; its typed exceptions become typed tool errors.
    /// </summary>
    public static class McpModelProbeTools
    {
        #region Private-Members

        private const string UnavailableMessage = "Evaluate is not available on this server.";

        private static readonly JsonSerializerOptions _JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        #endregion

        #region Public-Methods

        /// <summary>
        /// Register the tools. When <paramref name="service"/> is null every tool is still registered and answers a typed
        /// Unavailable error (the standalone stdio server), so the tool list matches the HTTP server's.
        /// </summary>
        /// <param name="register">Registration delegate.</param>
        /// <param name="service">Model probe access service, or null.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="register"/> is null.</exception>
        public static void Register(RegisterToolDelegate register, ModelProbeAccessService? service)
        {
            if (register == null) throw new ArgumentNullException(nameof(register));

            register(
                "evaluate_captain",
                "Start a Quick Evaluate: probe-only mechanical probes (output contract, served context, ASCII and format rules, tool calling, speed, determinism) through the captain's own runtime, or through the API endpoint runtime for a model endpoint. Give exactly one of captainId or modelEndpointId. Returns the queued run at once; poll get_model_probe_run. Probes record observations; a probe a runtime cannot run is CouldNotRun with the reason. Nothing changes routing or captain settings. Evaluating a captain needs a global admin or a tenant admin of its tenant; evaluating an endpoint needs read access to it. Depth Full is not implemented until Phase 5.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        captainId = new { type = "string", description = "Captain to evaluate (cpt_ prefix)" },
                        modelEndpointId = new { type = "string", description = "Model endpoint to evaluate (mep_ prefix)" },
                        depth = new { type = "string", description = "Quick (default); Full is not implemented until Phase 5" }
                    }
                },
                async (args) =>
                {
                    if (service == null) return (object)McpToolError.Unavailable(UnavailableMessage);
                    AuthContext caller = McpToolHelpers.ResolveCallerContext();
                    ModelProbeEvaluateArgs request = Read<ModelProbeEvaluateArgs>(args);
                    bool hasCaptain = !String.IsNullOrWhiteSpace(request.CaptainId);
                    bool hasEndpoint = !String.IsNullOrWhiteSpace(request.ModelEndpointId);
                    if (hasCaptain == hasEndpoint)
                        return (object)McpToolError.InvalidArgument("Give exactly one of captainId or modelEndpointId.");

                    ModelProbeDepthEnum depth = ModelProbeDepthEnum.Quick;
                    if (!String.IsNullOrWhiteSpace(request.Depth) && !EnumNames.TryParse(request.Depth, true, out depth))
                        return (object)McpToolError.InvalidArgument("depth must be one of " + McpToolHelpers.EnumNames<ModelProbeDepthEnum>() + ".");
                    if (depth == ModelProbeDepthEnum.Full)
                        return (object)McpToolError.InvalidArgument(ModelProbeService.FullNotImplementedMessage);

                    if (hasCaptain) return (object)await service.EvaluateCaptainAsync(caller, request.CaptainId, depth).ConfigureAwait(false);
                    return (object)await service.EvaluateEndpointAsync(caller, request.ModelEndpointId, depth).ConfigureAwait(false);
                });

            register(
                "get_model_probe_run",
                "Get one Evaluate run visible to the caller, with its per-probe results (Pass, Fail, or CouldNotRun with the reason) and the observations each probe recorded.",
                new
                {
                    type = "object",
                    properties = new { id = new { type = "string", description = "Run ID" } },
                    required = new[] { "id" }
                },
                async (args) =>
                {
                    if (service == null) return (object)McpToolError.Unavailable(UnavailableMessage);
                    AuthContext caller = McpToolHelpers.ResolveCallerContext();
                    ModelProbeRunIdArgs request = Read<ModelProbeRunIdArgs>(args);
                    return (object)await service.GetRunAsync(caller, request.Id).ConfigureAwait(false);
                });

            register(
                "list_model_probe_runs",
                "List Evaluate runs, newest first, one page at a time, without their per-probe results. A global admin sees every tenant; everyone else sees their own tenant. Returns an enumeration result (objects, totalRecords, totalPages).",
                new
                {
                    type = "object",
                    properties = new
                    {
                        captainId = new { type = "string", description = "Captain filter (cpt_ prefix)" },
                        modelEndpointId = new { type = "string", description = "Model endpoint filter (mep_ prefix)" },
                        status = new { type = "string", description = "Queued, Running, Completed, or Failed" },
                        pageNumber = new { type = "integer", description = "Page number (default 1)" },
                        pageSize = new { type = "integer", description = "Page size (1-1000, default 25)" }
                    }
                },
                async (args) =>
                {
                    if (service == null) return (object)McpToolError.Unavailable(UnavailableMessage);
                    AuthContext caller = McpToolHelpers.ResolveCallerContext();
                    ModelProbeRunListArgs request = Read<ModelProbeRunListArgs>(args);
                    Dictionary<string, string?> values = new Dictionary<string, string?>(StringComparer.Ordinal)
                    {
                        ["captainId"] = request.CaptainId,
                        ["modelEndpointId"] = request.ModelEndpointId,
                        ["status"] = request.Status,
                        ["pageNumber"] = request.PageNumber?.ToString(CultureInfo.InvariantCulture),
                        ["pageSize"] = (request.PageSize ?? 25).ToString(CultureInfo.InvariantCulture)
                    };
                    ModelProbeRunQuery query = ModelProbeRoutes.ParseQuery(key => values.TryGetValue(key, out string? v) ? v : null);
                    return (object)await service.ListRunsAsync(caller, query).ConfigureAwait(false);
                });
        }

        #endregion

        #region Private-Methods

        private static T Read<T>(JsonElement? args) where T : class, new()
        {
            if (!args.HasValue || args.Value.ValueKind != JsonValueKind.Object) return new T();
            try
            {
                return JsonSerializer.Deserialize<T>(args.Value, _JsonOptions) ?? new T();
            }
            catch (JsonException ex)
            {
                throw new ArgumentException("Invalid arguments: " + ex.Message, ex);
            }
        }

        #endregion
    }
}
