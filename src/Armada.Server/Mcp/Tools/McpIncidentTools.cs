namespace Armada.Server.Mcp.Tools
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;

    /// <summary>
    /// Registers MCP tools for operational incident management.
    /// </summary>
    public static class McpIncidentTools
    {
        private static readonly JsonSerializerOptions _JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter() }
        };

        /// <summary>
        /// Registers incident MCP tools.
        /// </summary>
        public static void Register(
            RegisterToolDelegate register,
            IncidentService incidentService,
            ObjectiveService? objectiveService = null)
        {
            if (register == null) throw new ArgumentNullException(nameof(register));
            if (incidentService == null) throw new ArgumentNullException(nameof(incidentService));

            register(
                "armada_list_incidents",
                "Enumerate operational incidents with optional vessel, environment, check-run, deployment, release, mission, voyage, status, severity, and free-text filters.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        vesselId = new { type = "string", description = "Optional vessel filter" },
                        environmentId = new { type = "string", description = "Optional environment filter" },
                        checkRunId = new { type = "string", description = "Optional check-run filter" },
                        deploymentId = new { type = "string", description = "Optional deployment filter" },
                        releaseId = new { type = "string", description = "Optional release filter" },
                        missionId = new { type = "string", description = "Optional mission filter" },
                        voyageId = new { type = "string", description = "Optional voyage filter" },
                        status = new { type = "string", description = "Optional status: Open, Monitoring, Mitigated, RolledBack, or Closed" },
                        severity = new { type = "string", description = "Optional severity: Critical, High, Medium, or Low" },
                        search = new { type = "string", description = "Optional free-text search" },
                        pageNumber = new { type = "integer", description = "Optional page number" },
                        pageSize = new { type = "integer", description = "Optional page size" }
                    }
                },
                async (args) =>
                {
                    IncidentQuery query = args.HasValue
                        ? JsonSerializer.Deserialize<IncidentQuery>(args.Value, _JsonOptions) ?? new IncidentQuery()
                        : new IncidentQuery();
                    AuthContext auth = McpToolHelpers.CreateDefaultTenantAdminContext();
                    return (object)await incidentService.EnumerateAsync(auth, query).ConfigureAwait(false);
                });

            register(
                "armada_get_incident",
                "Inspect one operational incident by ID.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        incidentId = new { type = "string", description = "Incident ID (inc_ prefix)" }
                    },
                    required = new[] { "incidentId" }
                },
                async (args) =>
                {
                    IncidentIdArgs request = JsonSerializer.Deserialize<IncidentIdArgs>(args!.Value, _JsonOptions)
                        ?? throw new InvalidOperationException("Could not deserialize IncidentIdArgs.");
                    if (String.IsNullOrWhiteSpace(request.IncidentId)) return (object)new { Error = "incidentId is required" };
                    AuthContext auth = McpToolHelpers.CreateDefaultTenantAdminContext();
                    Incident? incident = await incidentService.ReadAsync(auth, request.IncidentId).ConfigureAwait(false);
                    if (incident == null) return (object)new { Error = "Incident not found" };
                    return (object)incident;
                });

            register(
                "armada_create_incident",
                "Create an operational incident tied to delivery entities such as vessels, missions, voyages, checks, releases, or deployments.",
                BuildIncidentUpsertSchema(requireTitle: true),
                async (args) =>
                {
                    IncidentUpsertRequest request = JsonSerializer.Deserialize<IncidentUpsertRequest>(args!.Value, _JsonOptions)
                        ?? throw new InvalidOperationException("Could not deserialize IncidentUpsertRequest.");
                    AuthContext auth = McpToolHelpers.CreateDefaultTenantAdminContext();
                    await ValidateObjectiveLinksAsync(auth, objectiveService, request.ObjectiveIds).ConfigureAwait(false);
                    Incident incident = await incidentService.CreateAsync(auth, request).ConfigureAwait(false);
                    await LinkObjectiveIdsAsync(auth, objectiveService, request.ObjectiveIds, incident.Id).ConfigureAwait(false);
                    return (object)incident;
                });

            register(
                "armada_update_incident",
                "Update an operational incident's status, severity, impact, recovery notes, root cause, postmortem, or delivery links.",
                BuildIncidentUpdateSchema(),
                async (args) =>
                {
                    IncidentUpdateArgs request = JsonSerializer.Deserialize<IncidentUpdateArgs>(args!.Value, _JsonOptions)
                        ?? throw new InvalidOperationException("Could not deserialize IncidentUpdateArgs.");
                    if (String.IsNullOrWhiteSpace(request.IncidentId)) return (object)new { Error = "incidentId is required" };
                    AuthContext auth = McpToolHelpers.CreateDefaultTenantAdminContext();
                    IncidentUpsertRequest update = request.ToUpsertRequest();
                    await ValidateObjectiveLinksAsync(auth, objectiveService, update.ObjectiveIds).ConfigureAwait(false);
                    try
                    {
                        Incident incident = await incidentService.UpdateAsync(auth, request.IncidentId, update).ConfigureAwait(false);
                        await LinkObjectiveIdsAsync(auth, objectiveService, update.ObjectiveIds, incident.Id).ConfigureAwait(false);
                        return (object)incident;
                    }
                    catch (InvalidOperationException ex)
                    {
                        return (object)new { Error = ex.Message };
                    }
                });

            register(
                "armada_delete_incident",
                "Delete one operational incident and its event snapshots.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        incidentId = new { type = "string", description = "Incident ID (inc_ prefix)" }
                    },
                    required = new[] { "incidentId" }
                },
                async (args) =>
                {
                    IncidentIdArgs request = JsonSerializer.Deserialize<IncidentIdArgs>(args!.Value, _JsonOptions)
                        ?? throw new InvalidOperationException("Could not deserialize IncidentIdArgs.");
                    if (String.IsNullOrWhiteSpace(request.IncidentId)) return (object)new { Error = "incidentId is required" };
                    AuthContext auth = McpToolHelpers.CreateDefaultTenantAdminContext();
                    try
                    {
                        await incidentService.DeleteAsync(auth, request.IncidentId).ConfigureAwait(false);
                        return (object)new { Deleted = true, request.IncidentId };
                    }
                    catch (InvalidOperationException ex)
                    {
                        return (object)new { Error = ex.Message };
                    }
                });
        }

        private static object BuildIncidentUpsertSchema(bool requireTitle)
        {
            return new
            {
                type = "object",
                properties = new
                {
                    title = new { type = "string", description = "Incident title" },
                    summary = new { type = "string", description = "Optional short summary" },
                    status = new { type = "string", description = "Optional status: Open, Monitoring, Mitigated, RolledBack, or Closed" },
                    severity = new { type = "string", description = "Optional severity: Critical, High, Medium, or Low" },
                    environmentId = new { type = "string", description = "Optional environment ID" },
                    environmentName = new { type = "string", description = "Optional environment name" },
                    checkRunId = new { type = "string", description = "Optional check-run ID" },
                    deploymentId = new { type = "string", description = "Optional deployment ID" },
                    releaseId = new { type = "string", description = "Optional release ID" },
                    vesselId = new { type = "string", description = "Optional vessel ID" },
                    missionId = new { type = "string", description = "Optional mission ID" },
                    voyageId = new { type = "string", description = "Optional voyage ID" },
                    rollbackDeploymentId = new { type = "string", description = "Optional rollback deployment ID" },
                    objectiveIds = new { type = "array", items = new { type = "string" }, description = "Optional objective IDs to link" },
                    impact = new { type = "string", description = "Optional impact summary" },
                    rootCause = new { type = "string", description = "Optional root-cause notes" },
                    recoveryNotes = new { type = "string", description = "Optional recovery notes" },
                    postmortem = new { type = "string", description = "Optional postmortem notes" },
                    detectedUtc = new { type = "string", description = "Optional detection timestamp in UTC" },
                    mitigatedUtc = new { type = "string", description = "Optional mitigation timestamp in UTC" },
                    closedUtc = new { type = "string", description = "Optional closure timestamp in UTC" }
                },
                required = requireTitle ? new[] { "title" } : Array.Empty<string>()
            };
        }

        private static object BuildIncidentUpdateSchema()
        {
            return new
            {
                type = "object",
                properties = new
                {
                    incidentId = new { type = "string", description = "Incident ID (inc_ prefix)" },
                    title = new { type = "string", description = "Incident title" },
                    summary = new { type = "string", description = "Optional short summary" },
                    status = new { type = "string", description = "Optional status: Open, Monitoring, Mitigated, RolledBack, or Closed" },
                    severity = new { type = "string", description = "Optional severity: Critical, High, Medium, or Low" },
                    environmentId = new { type = "string", description = "Optional environment ID" },
                    environmentName = new { type = "string", description = "Optional environment name" },
                    checkRunId = new { type = "string", description = "Optional check-run ID" },
                    deploymentId = new { type = "string", description = "Optional deployment ID" },
                    releaseId = new { type = "string", description = "Optional release ID" },
                    vesselId = new { type = "string", description = "Optional vessel ID" },
                    missionId = new { type = "string", description = "Optional mission ID" },
                    voyageId = new { type = "string", description = "Optional voyage ID" },
                    rollbackDeploymentId = new { type = "string", description = "Optional rollback deployment ID" },
                    objectiveIds = new { type = "array", items = new { type = "string" }, description = "Optional objective IDs to link" },
                    impact = new { type = "string", description = "Optional impact summary" },
                    rootCause = new { type = "string", description = "Optional root-cause notes" },
                    recoveryNotes = new { type = "string", description = "Optional recovery notes" },
                    postmortem = new { type = "string", description = "Optional postmortem notes" },
                    mitigatedUtc = new { type = "string", description = "Optional mitigation timestamp in UTC" },
                    closedUtc = new { type = "string", description = "Optional closure timestamp in UTC" }
                },
                required = new[] { "incidentId" }
            };
        }

        private static async Task ValidateObjectiveLinksAsync(
            AuthContext auth,
            ObjectiveService? objectiveService,
            IEnumerable<string>? objectiveIds)
        {
            if (objectiveService == null || objectiveIds == null) return;

            foreach (string objectiveId in objectiveIds.Where(id => !String.IsNullOrWhiteSpace(id)))
            {
                Objective? objective = await objectiveService.ReadAsync(auth, objectiveId.Trim()).ConfigureAwait(false);
                if (objective == null) throw new InvalidOperationException("Objective not found: " + objectiveId.Trim());
            }
        }

        private static async Task LinkObjectiveIdsAsync(
            AuthContext auth,
            ObjectiveService? objectiveService,
            IEnumerable<string>? objectiveIds,
            string incidentId)
        {
            if (objectiveService == null || objectiveIds == null) return;

            foreach (string objectiveId in objectiveIds.Where(id => !String.IsNullOrWhiteSpace(id)))
            {
                await objectiveService.LinkIncidentAsync(auth, objectiveId.Trim(), incidentId).ConfigureAwait(false);
            }
        }

        private sealed class IncidentIdArgs
        {
            public string IncidentId { get; set; } = "";
        }

        private sealed class IncidentUpdateArgs : IncidentUpsertRequest
        {
            public string IncidentId { get; set; } = "";

            public IncidentUpsertRequest ToUpsertRequest()
            {
                return new IncidentUpsertRequest
                {
                    Title = Title,
                    Summary = Summary,
                    Status = Status,
                    Severity = Severity,
                    EnvironmentId = EnvironmentId,
                    EnvironmentName = EnvironmentName,
                    CheckRunId = CheckRunId,
                    DeploymentId = DeploymentId,
                    ReleaseId = ReleaseId,
                    VesselId = VesselId,
                    MissionId = MissionId,
                    VoyageId = VoyageId,
                    RollbackDeploymentId = RollbackDeploymentId,
                    ObjectiveIds = ObjectiveIds,
                    Impact = Impact,
                    RootCause = RootCause,
                    RecoveryNotes = RecoveryNotes,
                    Postmortem = Postmortem,
                    DetectedUtc = DetectedUtc,
                    MitigatedUtc = MitigatedUtc,
                    ClosedUtc = ClosedUtc
                };
            }
        }
    }
}
