namespace Armada.Server.Mcp.Tools
{
    using System;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using Armada.Core.Models;
    using Armada.Core.Services;

    /// <summary>
    /// Registers MCP tools for objective inspection and creation.
    /// </summary>
    public static class McpObjectiveTools
    {
        private static readonly JsonSerializerOptions _JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter() }
        };

        /// <summary>
        /// Registers objective MCP tools.
        /// </summary>
        public static void Register(RegisterToolDelegate register, ObjectiveService objectiveService)
        {
            register(
                "get_objective",
                "Inspect one objective including linked repositories, planning sessions, voyages, releases, deployments, incidents, and acceptance criteria.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        objectiveId = new { type = "string", description = "Objective ID (obj_ prefix)" }
                    },
                    required = new[] { "objectiveId" }
                },
                async (args) =>
                {
                    ObjectiveIdArgs request = JsonSerializer.Deserialize<ObjectiveIdArgs>(args!.Value, _JsonOptions)
                        ?? throw new InvalidOperationException("Could not deserialize ObjectiveIdArgs.");
                    AuthContext auth = McpToolHelpers.CreateDefaultTenantAdminContext();
                    Objective? objective = await objectiveService.ReadAsync(auth, request.ObjectiveId).ConfigureAwait(false);
                    if (objective == null) return (object)new { Error = "Objective not found" };
                    return (object)objective;
                });

            register(
                "create_objective",
                "Create an internal-first objective or intake-style record that can link vessels, planning sessions, voyages, checks, releases, deployments, and incidents.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        title = new { type = "string", description = "Objective title" },
                        description = new { type = "string", description = "Optional long-form description" },
                        status = new { type = "string", description = "Optional objective status such as Draft, Scoped, Planned, or InProgress" },
                        owner = new { type = "string", description = "Optional owner display label" },
                        tags = new { type = "array", items = new { type = "string" }, description = "Optional tags" },
                        acceptanceCriteria = new { type = "array", items = new { type = "string" }, description = "Acceptance criteria" },
                        nonGoals = new { type = "array", items = new { type = "string" }, description = "Explicit non-goals" },
                        rolloutConstraints = new { type = "array", items = new { type = "string" }, description = "Rollout constraints" },
                        evidenceLinks = new { type = "array", items = new { type = "string" }, description = "Evidence or source links" },
                        fleetIds = new { type = "array", items = new { type = "string" }, description = "Linked fleet IDs" },
                        vesselIds = new { type = "array", items = new { type = "string" }, description = "Linked vessel IDs" },
                        planningSessionIds = new { type = "array", items = new { type = "string" }, description = "Linked planning-session IDs" },
                        voyageIds = new { type = "array", items = new { type = "string" }, description = "Linked voyage IDs" },
                        missionIds = new { type = "array", items = new { type = "string" }, description = "Linked mission IDs" },
                        checkRunIds = new { type = "array", items = new { type = "string" }, description = "Linked check-run IDs" },
                        releaseIds = new { type = "array", items = new { type = "string" }, description = "Linked release IDs" },
                        deploymentIds = new { type = "array", items = new { type = "string" }, description = "Linked deployment IDs" },
                        incidentIds = new { type = "array", items = new { type = "string" }, description = "Linked incident IDs" }
                    },
                    required = new[] { "title" }
                },
                async (args) =>
                {
                    ObjectiveUpsertRequest request = JsonSerializer.Deserialize<ObjectiveUpsertRequest>(args!.Value, _JsonOptions)
                        ?? throw new InvalidOperationException("Could not deserialize ObjectiveUpsertRequest.");
                    AuthContext auth = McpToolHelpers.CreateDefaultTenantAdminContext();
                    return (object)await objectiveService.CreateAsync(auth, request).ConfigureAwait(false);
                });
        }
    }
}
