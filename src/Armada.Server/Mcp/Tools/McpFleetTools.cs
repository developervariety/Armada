namespace Armada.Server.Mcp.Tools
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Armada.Core;
    using ArmadaConstants = Armada.Core.Constants;
    using Armada.Core.Database;
    using Armada.Core.Models;

    /// <summary>
    /// Registers MCP tools for fleet CRUD operations.
    /// </summary>
    public static class McpFleetTools
    {
        private static readonly JsonSerializerOptions _JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        /// <summary>
        /// Registers fleet MCP tools with the server.
        /// </summary>
        /// <param name="register">Delegate to register each tool.</param>
        /// <param name="database">Database driver for fleet data access.</param>
        public static void Register(RegisterToolDelegate register, DatabaseDriver database)
        {
            register(
                "armada_get_fleet",
                "Get details of a specific fleet including its vessels. The response includes defaultPlaybooks (the fleet-scope playbook auto-merge layer).",
                new
                {
                    type = "object",
                    properties = new
                    {
                        fleetId = new { type = "string", description = "Fleet ID (flt_ prefix)" }
                    },
                    required = new[] { "fleetId" }
                },
                async (args) =>
                {
                    FleetIdArgs request = JsonSerializer.Deserialize<FleetIdArgs>(args!.Value, _JsonOptions)!;
                    string fleetId = request.FleetId;
                    Fleet? fleet = await database.Fleets.ReadAsync(fleetId).ConfigureAwait(false);
                    if (fleet == null) return (object)new { Error = "Fleet not found" };
                    List<Vessel> vessels = await database.Vessels.EnumerateByFleetAsync(fleetId).ConfigureAwait(false);
                    return (object)new
                    {
                        Fleet = fleet,
                        Vessels = vessels,
                        // Fleet-scope playbook auto-merge layer surfaced explicitly so MCP clients
                        // can rely on the documented response shape without depending on Fleet model
                        // serialization details.
                        defaultPlaybooks = fleet.GetDefaultPlaybooks()
                    };
                });

            register(
                "armada_create_fleet",
                "Create a new fleet (collection of repositories)",
                new
                {
                    type = "object",
                    properties = new
                    {
                        name = new { type = "string", description = "Fleet name" },
                        description = new { type = "string", description = "Fleet description" }
                    },
                    required = new[] { "name" }
                },
                async (args) =>
                {
                    FleetCreateArgs request = JsonSerializer.Deserialize<FleetCreateArgs>(args!.Value, _JsonOptions)!;
                    // The fleet is owned by the authenticated caller, exactly as a REST create is.
                    AuthContext caller = McpCallerContext.Require();
                    Fleet fleet = new Fleet();
                    fleet.TenantId = Armada.Core.Authorization.OwnershipPolicy.TenantOf(caller);
                    fleet.UserId = Armada.Core.Authorization.OwnershipPolicy.UserOf(caller);
                    fleet.Name = request.Name;
                    fleet.Description = request.Description ?? "";
                    fleet = await database.Fleets.CreateAsync(fleet).ConfigureAwait(false);
                    return (object)fleet;
                });

            register(
                "armada_update_fleet",
                "Update an existing fleet. Adds defaultPlaybooks (JSON array) to the editable fields.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        fleetId = new { type = "string", description = "Fleet ID (flt_ prefix)" },
                        name = new { type = "string", description = "New fleet name" },
                        description = new { type = "string", description = "New fleet description" },
                        defaultPipelineId = new { type = "string", description = "Default pipeline ID for dispatches to vessels in this fleet (ppl_ prefix)" },
                        defaultPlaybooks = new { type = "string", description = "JSON-serialized SelectedPlaybook list auto-merged into every mission for this fleet. Layered FIRST in the fleet -> vessel -> persona -> captain merge." }
                    },
                    required = new[] { "fleetId" }
                },
                async (args) =>
                {
                    FleetUpdateArgs request = JsonSerializer.Deserialize<FleetUpdateArgs>(args!.Value, _JsonOptions)!;
                    string fleetId = request.FleetId;
                    Fleet? fleet = await database.Fleets.ReadAsync(fleetId).ConfigureAwait(false);
                    if (fleet == null) return (object)new { Error = "Fleet not found" };
                    if (request.Name != null)
                        fleet.Name = request.Name;
                    if (request.Description != null)
                        fleet.Description = request.Description;
                    if (request.DefaultPipelineId != null)
                        fleet.DefaultPipelineId = request.DefaultPipelineId;
                    if (request.DefaultPlaybooks != null)
                        fleet.DefaultPlaybooks = request.DefaultPlaybooks;
                    fleet = await database.Fleets.UpdateAsync(fleet).ConfigureAwait(false);
                    return (object)fleet;
                });

            register(
                "armada_delete_fleet",
                "Delete a fleet by ID",
                new
                {
                    type = "object",
                    properties = new
                    {
                        fleetId = new { type = "string", description = "Fleet ID (flt_ prefix)" }
                    },
                    required = new[] { "fleetId" }
                },
                async (args) =>
                {
                    FleetIdArgs request = JsonSerializer.Deserialize<FleetIdArgs>(args!.Value, _JsonOptions)!;
                    string fleetId = request.FleetId;
                    bool exists = await database.Fleets.ExistsAsync(fleetId).ConfigureAwait(false);
                    if (!exists) return (object)new { Error = "Fleet not found" };
                    await database.Fleets.DeleteAsync(fleetId).ConfigureAwait(false);
                    return (object)new { Status = "deleted", FleetId = fleetId };
                });

            register(
                "armada_delete_fleets",
                "Permanently delete multiple fleets from the database by ID. Returns a summary of deleted and skipped entries. This cannot be undone.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        ids = new { type = "array", items = new { type = "string" }, description = "List of fleet IDs to delete (flt_ prefix)" }
                    },
                    required = new[] { "ids" }
                },
                async (args) =>
                {
                    DeleteMultipleArgs request = JsonSerializer.Deserialize<DeleteMultipleArgs>(args!.Value, _JsonOptions)!;
                    if (request.Ids == null || request.Ids.Count == 0)
                        return (object)new { Error = "ids is required and must not be empty" };

                    DeleteMultipleResult result = new DeleteMultipleResult();
                    foreach (string id in request.Ids)
                    {
                        if (String.IsNullOrEmpty(id))
                        {
                            result.Skipped.Add(new DeleteMultipleSkipped(id ?? "", "Empty ID"));
                            continue;
                        }
                        bool exists = await database.Fleets.ExistsAsync(id).ConfigureAwait(false);
                        if (!exists)
                        {
                            result.Skipped.Add(new DeleteMultipleSkipped(id, "Not found"));
                            continue;
                        }
                        await database.Fleets.DeleteAsync(id).ConfigureAwait(false);
                        result.Deleted++;
                    }
                    result.ResolveStatus();
                    return (object)result;
                });
        }
    }
}
