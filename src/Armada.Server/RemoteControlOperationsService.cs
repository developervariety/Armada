namespace Armada.Server
{
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using Armada.Core;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;

    /// <summary>
    /// Handles broader remote-control operations routed through the outbound tunnel.
    /// This surface covers backlog/planning, delivery workflows, diagnostics, and
    /// optional reference views used by the proxy shell.
    /// </summary>
    public class RemoteControlOperationsService
    {
        private static readonly JsonSerializerOptions _BodyJsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter() }
        };

        private static readonly AuthContext _ProxyAuth = AuthContext.Authenticated(
            Constants.DefaultTenantId,
            Constants.DefaultUserId,
            true,
            true,
            "ProxyTunnel",
            principalDisplay: "Armada Proxy");

        private readonly DatabaseDriver _Database;
        private readonly ObjectiveService _Objectives;
        private readonly ObjectiveRefinementCoordinator _ObjectiveRefinementSessions;
        private readonly PlanningSessionCoordinator _PlanningSessions;
        private readonly WorkflowProfileService _WorkflowProfiles;
        private readonly DeploymentEnvironmentService _Environments;
        private readonly CheckRunService _CheckRuns;
        private readonly ReleaseService _Releases;
        private readonly DeploymentService _Deployments;
        private readonly IncidentService _Incidents;
        private readonly RunbookService _Runbooks;
        private readonly CaptainToolService _CaptainTools;
        private readonly IWorkspaceService _Workspace;
        private readonly IPromptTemplateService _PromptTemplates;

        /// <summary>
        /// Instantiate.
        /// </summary>
        public RemoteControlOperationsService(
            DatabaseDriver database,
            ObjectiveService objectives,
            ObjectiveRefinementCoordinator objectiveRefinementSessions,
            PlanningSessionCoordinator planningSessions,
            WorkflowProfileService workflowProfiles,
            DeploymentEnvironmentService environments,
            CheckRunService checkRuns,
            ReleaseService releases,
            DeploymentService deployments,
            IncidentService incidents,
            RunbookService runbooks,
            CaptainToolService captainTools,
            IWorkspaceService workspace,
            IPromptTemplateService promptTemplates)
        {
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _Objectives = objectives ?? throw new ArgumentNullException(nameof(objectives));
            _ObjectiveRefinementSessions = objectiveRefinementSessions ?? throw new ArgumentNullException(nameof(objectiveRefinementSessions));
            _PlanningSessions = planningSessions ?? throw new ArgumentNullException(nameof(planningSessions));
            _WorkflowProfiles = workflowProfiles ?? throw new ArgumentNullException(nameof(workflowProfiles));
            _Environments = environments ?? throw new ArgumentNullException(nameof(environments));
            _CheckRuns = checkRuns ?? throw new ArgumentNullException(nameof(checkRuns));
            _Releases = releases ?? throw new ArgumentNullException(nameof(releases));
            _Deployments = deployments ?? throw new ArgumentNullException(nameof(deployments));
            _Incidents = incidents ?? throw new ArgumentNullException(nameof(incidents));
            _Runbooks = runbooks ?? throw new ArgumentNullException(nameof(runbooks));
            _CaptainTools = captainTools ?? throw new ArgumentNullException(nameof(captainTools));
            _Workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
            _PromptTemplates = promptTemplates ?? throw new ArgumentNullException(nameof(promptTemplates));
        }

        /// <summary>
        /// Handle a broad remote-control operations request.
        /// </summary>
        public async Task<RemoteTunnelRequestResult> HandleAsync(RemoteTunnelEnvelope envelope, CancellationToken token)
        {
            string method = envelope.Method?.Trim().ToLowerInvariant() ?? String.Empty;
            try
            {
                switch (method)
                {
                    case "armada.objectives.list":
                    case "armada.backlog.list":
                        return await ListObjectivesAsync(envelope, token).ConfigureAwait(false);
                    case "armada.objective.detail":
                    case "armada.backlog.detail":
                        return await GetObjectiveDetailAsync(envelope, token).ConfigureAwait(false);
                    case "armada.objective.create":
                    case "armada.backlog.create":
                        return await CreateObjectiveAsync(envelope, token).ConfigureAwait(false);
                    case "armada.objective.update":
                    case "armada.backlog.update":
                        return await UpdateObjectiveAsync(envelope, token).ConfigureAwait(false);
                    case "armada.objective.delete":
                    case "armada.backlog.delete":
                        return await DeleteObjectiveAsync(envelope, token).ConfigureAwait(false);
                    case "armada.objective-refinement-sessions.list":
                        return await ListObjectiveRefinementSessionsAsync(envelope, token).ConfigureAwait(false);
                    case "armada.objective-refinement-sessions.create":
                        return await CreateObjectiveRefinementSessionAsync(envelope, token).ConfigureAwait(false);
                    case "armada.objective-refinement-session.detail":
                        return await GetObjectiveRefinementSessionDetailAsync(envelope, token).ConfigureAwait(false);
                    case "armada.objective-refinement-session.message":
                        return await SendObjectiveRefinementMessageAsync(envelope, token).ConfigureAwait(false);
                    case "armada.objective-refinement-session.summarize":
                        return await SummarizeObjectiveRefinementSessionAsync(envelope, token).ConfigureAwait(false);
                    case "armada.objective-refinement-session.apply":
                        return await ApplyObjectiveRefinementSessionAsync(envelope, token).ConfigureAwait(false);
                    case "armada.objective-refinement-session.stop":
                        return await StopObjectiveRefinementSessionAsync(envelope, token).ConfigureAwait(false);
                    case "armada.objective-refinement-session.delete":
                        return await DeleteObjectiveRefinementSessionAsync(envelope, token).ConfigureAwait(false);
                    case "armada.planning-sessions.list":
                        return await ListPlanningSessionsAsync(envelope, token).ConfigureAwait(false);
                    case "armada.planning-session.detail":
                        return await GetPlanningSessionDetailAsync(envelope, token).ConfigureAwait(false);
                    case "armada.planning-session.create":
                        return await CreatePlanningSessionAsync(envelope, token).ConfigureAwait(false);
                    case "armada.planning-session.message":
                        return await SendPlanningSessionMessageAsync(envelope, token).ConfigureAwait(false);
                    case "armada.planning-session.summarize":
                        return await SummarizePlanningSessionAsync(envelope, token).ConfigureAwait(false);
                    case "armada.planning-session.dispatch":
                        return await DispatchPlanningSessionAsync(envelope, token).ConfigureAwait(false);
                    case "armada.planning-session.stop":
                        return await StopPlanningSessionAsync(envelope, token).ConfigureAwait(false);
                    case "armada.planning-session.delete":
                        return await DeletePlanningSessionAsync(envelope, token).ConfigureAwait(false);
                    case "armada.workflow-profiles.list":
                        return await ListWorkflowProfilesAsync(envelope, token).ConfigureAwait(false);
                    case "armada.workflow-profile.detail":
                        return await GetWorkflowProfileDetailAsync(envelope, token).ConfigureAwait(false);
                    case "armada.workflow-profile.create":
                        return await CreateWorkflowProfileAsync(envelope, token).ConfigureAwait(false);
                    case "armada.workflow-profile.update":
                        return await UpdateWorkflowProfileAsync(envelope, token).ConfigureAwait(false);
                    case "armada.workflow-profile.delete":
                        return await DeleteWorkflowProfileAsync(envelope, token).ConfigureAwait(false);
                    case "armada.check-runs.list":
                        return await ListCheckRunsAsync(envelope, token).ConfigureAwait(false);
                    case "armada.check-run.detail":
                        return await GetCheckRunDetailAsync(envelope, token).ConfigureAwait(false);
                    case "armada.check-run.create":
                        return await CreateCheckRunAsync(envelope, token).ConfigureAwait(false);
                    case "armada.check-run.retry":
                        return await RetryCheckRunAsync(envelope, token).ConfigureAwait(false);
                    case "armada.check-run.delete":
                        return await DeleteCheckRunAsync(envelope, token).ConfigureAwait(false);
                    case "armada.environments.list":
                        return await ListEnvironmentsAsync(envelope, token).ConfigureAwait(false);
                    case "armada.environment.detail":
                        return await GetEnvironmentDetailAsync(envelope, token).ConfigureAwait(false);
                    case "armada.environment.create":
                        return await CreateEnvironmentAsync(envelope, token).ConfigureAwait(false);
                    case "armada.environment.update":
                        return await UpdateEnvironmentAsync(envelope, token).ConfigureAwait(false);
                    case "armada.environment.delete":
                        return await DeleteEnvironmentAsync(envelope, token).ConfigureAwait(false);
                    case "armada.releases.list":
                        return await ListReleasesAsync(envelope, token).ConfigureAwait(false);
                    case "armada.release.detail":
                        return await GetReleaseDetailAsync(envelope, token).ConfigureAwait(false);
                    case "armada.release.create":
                        return await CreateReleaseAsync(envelope, token).ConfigureAwait(false);
                    case "armada.release.update":
                        return await UpdateReleaseAsync(envelope, token).ConfigureAwait(false);
                    case "armada.release.refresh":
                        return await RefreshReleaseAsync(envelope, token).ConfigureAwait(false);
                    case "armada.release.delete":
                        return await DeleteReleaseAsync(envelope, token).ConfigureAwait(false);
                    case "armada.deployments.list":
                        return await ListDeploymentsAsync(envelope, token).ConfigureAwait(false);
                    case "armada.deployment.detail":
                        return await GetDeploymentDetailAsync(envelope, token).ConfigureAwait(false);
                    case "armada.deployment.create":
                        return await CreateDeploymentAsync(envelope, token).ConfigureAwait(false);
                    case "armada.deployment.update":
                        return await UpdateDeploymentAsync(envelope, token).ConfigureAwait(false);
                    case "armada.deployment.approve":
                        return await ApproveDeploymentAsync(envelope, token).ConfigureAwait(false);
                    case "armada.deployment.deny":
                        return await DenyDeploymentAsync(envelope, token).ConfigureAwait(false);
                    case "armada.deployment.verify":
                        return await VerifyDeploymentAsync(envelope, token).ConfigureAwait(false);
                    case "armada.deployment.rollback":
                        return await RollbackDeploymentAsync(envelope, token).ConfigureAwait(false);
                    case "armada.deployment.delete":
                        return await DeleteDeploymentAsync(envelope, token).ConfigureAwait(false);
                    case "armada.incidents.list":
                        return await ListIncidentsAsync(envelope, token).ConfigureAwait(false);
                    case "armada.incident.detail":
                        return await GetIncidentDetailAsync(envelope, token).ConfigureAwait(false);
                    case "armada.incident.create":
                        return await CreateIncidentAsync(envelope, token).ConfigureAwait(false);
                    case "armada.incident.update":
                        return await UpdateIncidentAsync(envelope, token).ConfigureAwait(false);
                    case "armada.incident.delete":
                        return await DeleteIncidentAsync(envelope, token).ConfigureAwait(false);
                    case "armada.runbooks.list":
                        return await ListRunbooksAsync(envelope, token).ConfigureAwait(false);
                    case "armada.runbook.detail":
                        return await GetRunbookDetailAsync(envelope, token).ConfigureAwait(false);
                    case "armada.runbook.create":
                        return await CreateRunbookAsync(envelope, token).ConfigureAwait(false);
                    case "armada.runbook.update":
                        return await UpdateRunbookAsync(envelope, token).ConfigureAwait(false);
                    case "armada.runbook.delete":
                        return await DeleteRunbookAsync(envelope, token).ConfigureAwait(false);
                    case "armada.runbook-executions.list":
                        return await ListRunbookExecutionsAsync(envelope, token).ConfigureAwait(false);
                    case "armada.runbook-execution.detail":
                        return await GetRunbookExecutionDetailAsync(envelope, token).ConfigureAwait(false);
                    case "armada.runbook-execution.create":
                        return await CreateRunbookExecutionAsync(envelope, token).ConfigureAwait(false);
                    case "armada.runbook-execution.update":
                        return await UpdateRunbookExecutionAsync(envelope, token).ConfigureAwait(false);
                    case "armada.runbook-execution.delete":
                        return await DeleteRunbookExecutionAsync(envelope, token).ConfigureAwait(false);
                    case "armada.captain.tools":
                        return await GetCaptainToolsAsync(envelope, token).ConfigureAwait(false);
                    case "armada.request-history.list":
                        return await ListRequestHistoryAsync(envelope, token).ConfigureAwait(false);
                    case "armada.request-history.detail":
                        return await GetRequestHistoryDetailAsync(envelope, token).ConfigureAwait(false);
                    case "armada.request-history.summary":
                        return await SummarizeRequestHistoryAsync(envelope, token).ConfigureAwait(false);
                    case "armada.workspace.status":
                        return await GetWorkspaceStatusAsync(envelope, token).ConfigureAwait(false);
                    case "armada.workspace.tree":
                        return await GetWorkspaceTreeAsync(envelope, token).ConfigureAwait(false);
                    case "armada.workspace.file":
                        return await GetWorkspaceFileAsync(envelope, token).ConfigureAwait(false);
                    case "armada.workspace.search":
                        return await SearchWorkspaceAsync(envelope, token).ConfigureAwait(false);
                    case "armada.workspace.changes":
                        return await GetWorkspaceChangesAsync(envelope, token).ConfigureAwait(false);
                    case "armada.pipelines.list":
                        return await ListPipelinesAsync(envelope, token).ConfigureAwait(false);
                    case "armada.pipeline.detail":
                        return await GetPipelineDetailAsync(envelope, token).ConfigureAwait(false);
                    case "armada.personas.list":
                        return await ListPersonasAsync(envelope, token).ConfigureAwait(false);
                    case "armada.persona.detail":
                        return await GetPersonaDetailAsync(envelope, token).ConfigureAwait(false);
                    case "armada.prompt-templates.list":
                        return await ListPromptTemplatesAsync(envelope, token).ConfigureAwait(false);
                    case "armada.prompt-template.detail":
                        return await GetPromptTemplateDetailAsync(envelope, token).ConfigureAwait(false);
                    default:
                        return Unsupported(envelope.Method);
                }
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest("invalid_request", ex.Message);
            }
        }

        private async Task<RemoteTunnelRequestResult> ListObjectivesAsync(RemoteTunnelEnvelope envelope, CancellationToken token)
        {
            ObjectiveQuery query = DeserializePayload<ObjectiveQuery>(envelope) ?? new ObjectiveQuery();
            query.PageNumber = Math.Max(1, query.PageNumber);
            query.PageSize = Math.Clamp(query.PageSize <= 0 ? 25 : query.PageSize, 1, 200);
            return Ok(await _Objectives.EnumerateAsync(_ProxyAuth, query, token).ConfigureAwait(false), "Objectives captured.");
        }

        private async Task<RemoteTunnelRequestResult> GetObjectiveDetailAsync(RemoteTunnelEnvelope envelope, CancellationToken token)
        {
            string id = RequireId(envelope, "objective");
            Objective? objective = await _Objectives.ReadAsync(_ProxyAuth, id, token).ConfigureAwait(false);
            return objective == null ? NotFound("Objective not found.") : Ok(objective, "Objective captured.");
        }

        private async Task<RemoteTunnelRequestResult> CreateObjectiveAsync(RemoteTunnelEnvelope envelope, CancellationToken token)
        {
            ObjectiveUpsertRequest request = RequireBody<ObjectiveUpsertRequest>(envelope, "objective");
            try
            {
                Objective created = await _Objectives.CreateAsync(_ProxyAuth, request, token).ConfigureAwait(false);
                return Created(created, "Objective created.");
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest("invalid_objective", ex.Message);
            }
        }

        private async Task<RemoteTunnelRequestResult> UpdateObjectiveAsync(RemoteTunnelEnvelope envelope, CancellationToken token)
        {
            string id = RequireId(envelope, "objective");
            ObjectiveUpsertRequest request = RequireBody<ObjectiveUpsertRequest>(envelope, "objective");
            try
            {
                Objective updated = await _Objectives.UpdateAsync(_ProxyAuth, id, request, token).ConfigureAwait(false);
                return Ok(updated, "Objective updated.");
            }
            catch (InvalidOperationException ex)
            {
                return ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase)
                    ? NotFound(ex.Message)
                    : BadRequest("invalid_objective", ex.Message);
            }
        }

        private async Task<RemoteTunnelRequestResult> DeleteObjectiveAsync(RemoteTunnelEnvelope envelope, CancellationToken token)
        {
            string id = RequireId(envelope, "objective");
            try
            {
                await _Objectives.DeleteAsync(_ProxyAuth, id, token).ConfigureAwait(false);
                return NoContent("Objective deleted.");
            }
            catch (InvalidOperationException ex)
            {
                return NotFound(ex.Message);
            }
        }

        private async Task<RemoteTunnelRequestResult> ListObjectiveRefinementSessionsAsync(RemoteTunnelEnvelope envelope, CancellationToken token)
        {
            string objectiveId = RequireId(envelope, "objective");
            Objective? objective = await _Objectives.ReadAsync(_ProxyAuth, objectiveId, token).ConfigureAwait(false);
            if (objective == null)
            {
                return NotFound("Objective not found.");
            }

            List<ObjectiveRefinementSession> sessions = (await _Database.ObjectiveRefinementSessions.EnumerateAsync(token).ConfigureAwait(false))
                .Where(session => String.Equals(session.ObjectiveId, objective.Id, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(session => session.LastUpdateUtc)
                .ToList();

            return Ok(sessions, "Objective refinement sessions captured.");
        }

        private async Task<RemoteTunnelRequestResult> CreateObjectiveRefinementSessionAsync(RemoteTunnelEnvelope envelope, CancellationToken token)
        {
            string objectiveId = RequireId(envelope, "objective");
            Objective? objective = await _Objectives.ReadAsync(_ProxyAuth, objectiveId, token).ConfigureAwait(false);
            if (objective == null)
            {
                return NotFound("Objective not found.");
            }

            ObjectiveRefinementSessionCreateRequest request = RequireBody<ObjectiveRefinementSessionCreateRequest>(envelope, "objective refinement session");
            Captain? captain = await _Database.Captains.ReadAsync(request.CaptainId, token).ConfigureAwait(false);
            if (captain == null)
            {
                return NotFound("Captain not found.");
            }

            Vessel? vessel = null;
            string? vesselId = !String.IsNullOrWhiteSpace(request.VesselId)
                ? request.VesselId
                : objective.VesselIds.FirstOrDefault();
            if (!String.IsNullOrWhiteSpace(vesselId))
            {
                vessel = await _Database.Vessels.ReadAsync(vesselId, token).ConfigureAwait(false);
                if (vessel == null)
                {
                    return NotFound("Vessel not found.");
                }
            }

            try
            {
                ObjectiveRefinementSession session = await _ObjectiveRefinementSessions
                    .CreateAsync(_ProxyAuth.TenantId, _ProxyAuth.UserId, objective, captain, vessel, request)
                    .ConfigureAwait(false);
                await _Objectives.LinkRefinementSessionAsync(_ProxyAuth, objective.Id, session.Id, token).ConfigureAwait(false);
                return Created(await BuildObjectiveRefinementDetailAsync(session.Id, token).ConfigureAwait(false), "Objective refinement session created.");
            }
            catch (NotSupportedException ex)
            {
                return Error(501, "runtime_unsupported", ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                return Conflict(ex.Message);
            }
        }

        private async Task<RemoteTunnelRequestResult> GetObjectiveRefinementSessionDetailAsync(RemoteTunnelEnvelope envelope, CancellationToken token)
        {
            string id = RequireId(envelope, "objective refinement session");
            ObjectiveRefinementSession? session = await _Database.ObjectiveRefinementSessions.ReadAsync(id, token).ConfigureAwait(false);
            return session == null
                ? NotFound("Objective refinement session not found.")
                : Ok(await BuildObjectiveRefinementDetailAsync(id, token).ConfigureAwait(false), "Objective refinement session captured.");
        }

        private async Task<RemoteTunnelRequestResult> SendObjectiveRefinementMessageAsync(RemoteTunnelEnvelope envelope, CancellationToken token)
        {
            string id = RequireId(envelope, "objective refinement session");
            ObjectiveRefinementSession? session = await _Database.ObjectiveRefinementSessions.ReadAsync(id, token).ConfigureAwait(false);
            if (session == null)
            {
                return NotFound("Objective refinement session not found.");
            }

            ObjectiveRefinementMessageRequest request = RequireBody<ObjectiveRefinementMessageRequest>(envelope, "objective refinement message");
            if (String.IsNullOrWhiteSpace(request.Content))
            {
                return BadRequest("invalid_message", "Content is required.");
            }

            try
            {
                await _ObjectiveRefinementSessions.SendMessageAsync(session, request.Content).ConfigureAwait(false);
                return Ok(await BuildObjectiveRefinementDetailAsync(id, token).ConfigureAwait(false), "Objective refinement session updated.");
            }
            catch (NotSupportedException ex)
            {
                return Error(501, "runtime_unsupported", ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                return Conflict(ex.Message);
            }
        }

        private async Task<RemoteTunnelRequestResult> SummarizeObjectiveRefinementSessionAsync(RemoteTunnelEnvelope envelope, CancellationToken token)
        {
            string id = RequireId(envelope, "objective refinement session");
            ObjectiveRefinementSession? session = await _Database.ObjectiveRefinementSessions.ReadAsync(id, token).ConfigureAwait(false);
            if (session == null)
            {
                return NotFound("Objective refinement session not found.");
            }

            ObjectiveRefinementSummaryRequest request = DeserializeBodyElement<ObjectiveRefinementSummaryRequest>(ReadBodyElement(envelope)) ?? new ObjectiveRefinementSummaryRequest();
            try
            {
                return Ok(await _ObjectiveRefinementSessions.SummarizeAsync(session, request).ConfigureAwait(false), "Objective refinement summary captured.");
            }
            catch (NotSupportedException ex)
            {
                return Error(501, "runtime_unsupported", ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                return Conflict(ex.Message);
            }
        }

        private async Task<RemoteTunnelRequestResult> ApplyObjectiveRefinementSessionAsync(RemoteTunnelEnvelope envelope, CancellationToken token)
        {
            string id = RequireId(envelope, "objective refinement session");
            ObjectiveRefinementSession? session = await _Database.ObjectiveRefinementSessions.ReadAsync(id, token).ConfigureAwait(false);
            if (session == null)
            {
                return NotFound("Objective refinement session not found.");
            }

            Objective? objective = await _Objectives.ReadAsync(_ProxyAuth, session.ObjectiveId, token).ConfigureAwait(false);
            if (objective == null)
            {
                return NotFound("Objective not found.");
            }

            ObjectiveRefinementApplyRequest request = DeserializeBodyElement<ObjectiveRefinementApplyRequest>(ReadBodyElement(envelope)) ?? new ObjectiveRefinementApplyRequest();
            try
            {
                (ObjectiveRefinementSummaryResponse Summary, Objective Objective) result = await _ObjectiveRefinementSessions
                    .ApplyAsync(_ProxyAuth, objective, session, request, _Objectives, token)
                    .ConfigureAwait(false);

                return Ok(new ObjectiveRefinementApplyResponse
                {
                    Summary = result.Summary,
                    Objective = result.Objective
                }, "Objective refinement applied.");
            }
            catch (NotSupportedException ex)
            {
                return Error(501, "runtime_unsupported", ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                return Conflict(ex.Message);
            }
        }

        private async Task<RemoteTunnelRequestResult> StopObjectiveRefinementSessionAsync(RemoteTunnelEnvelope envelope, CancellationToken token)
        {
            string id = RequireId(envelope, "objective refinement session");
            ObjectiveRefinementSession? session = await _Database.ObjectiveRefinementSessions.ReadAsync(id, token).ConfigureAwait(false);
            if (session == null)
            {
                return NotFound("Objective refinement session not found.");
            }

            try
            {
                ObjectiveRefinementSession stopping = await _ObjectiveRefinementSessions.RequestStopAsync(session).ConfigureAwait(false);
                return Ok(await BuildObjectiveRefinementDetailAsync(stopping.Id, token).ConfigureAwait(false), "Objective refinement session stopped.");
            }
            catch (NotSupportedException ex)
            {
                return Error(501, "runtime_unsupported", ex.Message);
            }
        }

        private async Task<RemoteTunnelRequestResult> DeleteObjectiveRefinementSessionAsync(RemoteTunnelEnvelope envelope, CancellationToken token)
        {
            string id = RequireId(envelope, "objective refinement session");
            ObjectiveRefinementSession? session = await _Database.ObjectiveRefinementSessions.ReadAsync(id, token).ConfigureAwait(false);
            if (session == null)
            {
                return NotFound("Objective refinement session not found.");
            }

            try
            {
                await _ObjectiveRefinementSessions.DeleteAsync(session).ConfigureAwait(false);
                await _Objectives.UnlinkRefinementSessionAsync(_ProxyAuth, session.ObjectiveId, session.Id, token).ConfigureAwait(false);
                return NoContent("Objective refinement session deleted.");
            }
            catch (NotSupportedException ex)
            {
                return Error(501, "runtime_unsupported", ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                return Conflict(ex.Message);
            }
        }

        private async Task<RemoteTunnelRequestResult> ListPlanningSessionsAsync(RemoteTunnelEnvelope envelope, CancellationToken token)
        {
            PlanningSessionListRequest request = DeserializePayload<PlanningSessionListRequest>(envelope) ?? new PlanningSessionListRequest();
            IEnumerable<PlanningSession> sessions = (await _Database.PlanningSessions.EnumerateAsync(token).ConfigureAwait(false))
                .OrderByDescending(session => session.LastUpdateUtc);

            if (!String.IsNullOrWhiteSpace(request.CaptainId))
                sessions = sessions.Where(session => String.Equals(session.CaptainId, request.CaptainId, StringComparison.OrdinalIgnoreCase));
            if (!String.IsNullOrWhiteSpace(request.VesselId))
                sessions = sessions.Where(session => String.Equals(session.VesselId, request.VesselId, StringComparison.OrdinalIgnoreCase));
            if (!String.IsNullOrWhiteSpace(request.Status) && Enum.TryParse(request.Status, true, out PlanningSessionStatusEnum status))
                sessions = sessions.Where(session => session.Status == status);
            if (!String.IsNullOrWhiteSpace(request.ObjectiveId))
            {
                Objective? objective = await _Objectives.ReadAsync(_ProxyAuth, request.ObjectiveId, token).ConfigureAwait(false);
                if (objective != null)
                {
                    HashSet<string> linked = new HashSet<string>(objective.PlanningSessionIds ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
                    sessions = sessions.Where(session => linked.Contains(session.Id));
                }
                else
                {
                    sessions = Enumerable.Empty<PlanningSession>();
                }
            }

            int limit = request.Limit <= 0 ? 50 : Math.Clamp(request.Limit, 1, 200);
            return Ok(sessions.Take(limit).ToList(), "Planning sessions captured.");
        }

        private async Task<RemoteTunnelRequestResult> GetPlanningSessionDetailAsync(RemoteTunnelEnvelope envelope, CancellationToken token)
        {
            string id = RequireId(envelope, "planning session");
            PlanningSession? session = await _Database.PlanningSessions.ReadAsync(id, token).ConfigureAwait(false);
            return session == null
                ? NotFound("Planning session not found.")
                : Ok(await BuildPlanningSessionDetailAsync(id, token).ConfigureAwait(false), "Planning session captured.");
        }

        private async Task<RemoteTunnelRequestResult> CreatePlanningSessionAsync(RemoteTunnelEnvelope envelope, CancellationToken token)
        {
            PlanningSessionCreateRequest request = RequireBody<PlanningSessionCreateRequest>(envelope, "planning session");
            if (String.IsNullOrWhiteSpace(request.CaptainId) || String.IsNullOrWhiteSpace(request.VesselId))
            {
                return BadRequest("invalid_planning_session", "CaptainId and VesselId are required.");
            }

            Captain? captain = await _Database.Captains.ReadAsync(request.CaptainId, token).ConfigureAwait(false);
            if (captain == null)
            {
                return NotFound("Captain not found.");
            }

            Vessel? vessel = await _Database.Vessels.ReadAsync(request.VesselId, token).ConfigureAwait(false);
            if (vessel == null)
            {
                return NotFound("Vessel not found.");
            }

            if (!String.IsNullOrWhiteSpace(request.ObjectiveId))
            {
                Objective? objective = await _Objectives.ReadAsync(_ProxyAuth, request.ObjectiveId, token).ConfigureAwait(false);
                if (objective == null)
                {
                    return NotFound("Objective not found.");
                }
            }

            try
            {
                PlanningSession session = await _PlanningSessions
                    .CreateAsync(_ProxyAuth.TenantId, _ProxyAuth.UserId, captain, vessel, request, token)
                    .ConfigureAwait(false);
                if (!String.IsNullOrWhiteSpace(request.ObjectiveId))
                {
                    await _Objectives.LinkPlanningSessionAsync(_ProxyAuth, request.ObjectiveId, session.Id, token).ConfigureAwait(false);
                }

                return Created(await BuildPlanningSessionDetailAsync(session.Id, token).ConfigureAwait(false), "Planning session created.");
            }
            catch (NotSupportedException ex)
            {
                return Error(501, "runtime_unsupported", ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                return Conflict(ex.Message);
            }
        }

        private async Task<RemoteTunnelRequestResult> SendPlanningSessionMessageAsync(RemoteTunnelEnvelope envelope, CancellationToken token)
        {
            string id = RequireId(envelope, "planning session");
            PlanningSession? session = await _Database.PlanningSessions.ReadAsync(id, token).ConfigureAwait(false);
            if (session == null)
            {
                return NotFound("Planning session not found.");
            }

            PlanningSessionMessageRequest request = RequireBody<PlanningSessionMessageRequest>(envelope, "planning message");
            if (String.IsNullOrWhiteSpace(request.Content))
            {
                return BadRequest("invalid_planning_message", "Content is required.");
            }

            try
            {
                await _PlanningSessions.SendMessageAsync(session, request.Content).ConfigureAwait(false);
                return Ok(await BuildPlanningSessionDetailAsync(id, token).ConfigureAwait(false), "Planning session updated.");
            }
            catch (NotSupportedException ex)
            {
                return Error(501, "runtime_unsupported", ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                return Conflict(ex.Message);
            }
        }

        private async Task<RemoteTunnelRequestResult> SummarizePlanningSessionAsync(RemoteTunnelEnvelope envelope, CancellationToken token)
        {
            string id = RequireId(envelope, "planning session");
            PlanningSession? session = await _Database.PlanningSessions.ReadAsync(id, token).ConfigureAwait(false);
            if (session == null)
            {
                return NotFound("Planning session not found.");
            }

            PlanningSessionSummaryRequest request = DeserializeBodyElement<PlanningSessionSummaryRequest>(ReadBodyElement(envelope)) ?? new PlanningSessionSummaryRequest();
            try
            {
                return Ok(await _PlanningSessions.SummarizeAsync(session, request).ConfigureAwait(false), "Planning summary captured.");
            }
            catch (NotSupportedException ex)
            {
                return Error(501, "runtime_unsupported", ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                return Conflict(ex.Message);
            }
        }

        private async Task<RemoteTunnelRequestResult> DispatchPlanningSessionAsync(RemoteTunnelEnvelope envelope, CancellationToken token)
        {
            string id = RequireId(envelope, "planning session");
            PlanningSession? session = await _Database.PlanningSessions.ReadAsync(id, token).ConfigureAwait(false);
            if (session == null)
            {
                return NotFound("Planning session not found.");
            }

            PlanningSessionDispatchRequest request = DeserializeBodyElement<PlanningSessionDispatchRequest>(ReadBodyElement(envelope)) ?? new PlanningSessionDispatchRequest();
            try
            {
                Voyage voyage = await _PlanningSessions.DispatchAsync(session, request).ConfigureAwait(false);
                List<Objective> linkedObjectives = await _Objectives.EnumerateByPlanningSessionAsync(_ProxyAuth, session.Id, token).ConfigureAwait(false);
                foreach (Objective objective in linkedObjectives)
                {
                    await _Objectives.LinkVoyageAsync(_ProxyAuth, objective.Id, voyage.Id, token).ConfigureAwait(false);
                }

                return Ok(voyage, "Planning session dispatched.");
            }
            catch (NotSupportedException ex)
            {
                return Error(501, "runtime_unsupported", ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                return Conflict(ex.Message);
            }
        }

        private async Task<RemoteTunnelRequestResult> StopPlanningSessionAsync(RemoteTunnelEnvelope envelope, CancellationToken token)
        {
            string id = RequireId(envelope, "planning session");
            PlanningSession? session = await _Database.PlanningSessions.ReadAsync(id, token).ConfigureAwait(false);
            if (session == null)
            {
                return NotFound("Planning session not found.");
            }

            try
            {
                PlanningSession stopping = await _PlanningSessions.RequestStopAsync(session).ConfigureAwait(false);
                return Ok(await BuildPlanningSessionDetailAsync(stopping.Id, token).ConfigureAwait(false), "Planning session stopped.");
            }
            catch (NotSupportedException ex)
            {
                return Error(501, "runtime_unsupported", ex.Message);
            }
        }

        private async Task<RemoteTunnelRequestResult> DeletePlanningSessionAsync(RemoteTunnelEnvelope envelope, CancellationToken token)
        {
            string id = RequireId(envelope, "planning session");
            PlanningSession? session = await _Database.PlanningSessions.ReadAsync(id, token).ConfigureAwait(false);
            if (session == null)
            {
                return NotFound("Planning session not found.");
            }

            try
            {
                await _PlanningSessions.DeleteAsync(session).ConfigureAwait(false);
                return NoContent("Planning session deleted.");
            }
            catch (NotSupportedException ex)
            {
                return Error(501, "runtime_unsupported", ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                return Conflict(ex.Message);
            }
        }

        private async Task<RemoteTunnelRequestResult> ListWorkflowProfilesAsync(RemoteTunnelEnvelope envelope, CancellationToken token)
        {
            WorkflowProfileQuery query = DeserializePayload<WorkflowProfileQuery>(envelope) ?? new WorkflowProfileQuery();
            query.PageNumber = Math.Max(1, query.PageNumber);
            query.PageSize = Math.Clamp(query.PageSize <= 0 ? 25 : query.PageSize, 1, 200);
            return Ok(await _Database.WorkflowProfiles.EnumerateAsync(query, token).ConfigureAwait(false), "Workflow profiles captured.");
        }

        private async Task<RemoteTunnelRequestResult> GetWorkflowProfileDetailAsync(RemoteTunnelEnvelope envelope, CancellationToken token)
        {
            string id = RequireId(envelope, "workflow profile");
            WorkflowProfile? profile = await _Database.WorkflowProfiles.ReadAsync(id, new WorkflowProfileQuery(), token).ConfigureAwait(false);
            return profile == null ? NotFound("Workflow profile not found.") : Ok(profile, "Workflow profile captured.");
        }

        private async Task<RemoteTunnelRequestResult> CreateWorkflowProfileAsync(RemoteTunnelEnvelope envelope, CancellationToken token)
        {
            WorkflowProfile profile = RequireBody<WorkflowProfile>(envelope, "workflow profile");
            profile.TenantId ??= _ProxyAuth.TenantId;
            profile.UserId ??= _ProxyAuth.UserId;
            WorkflowProfileValidationResult validation = await _WorkflowProfiles.ValidateAsync(profile, token).ConfigureAwait(false);
            if (!validation.IsValid)
            {
                return BadRequest("invalid_workflow_profile", String.Join(" ", validation.Errors));
            }

            await EnsureUniqueDefaultWorkflowProfileAsync(profile, token).ConfigureAwait(false);
            WorkflowProfile created = await _Database.WorkflowProfiles.CreateAsync(profile, token).ConfigureAwait(false);
            return Created(created, "Workflow profile created.");
        }

        private async Task<RemoteTunnelRequestResult> UpdateWorkflowProfileAsync(RemoteTunnelEnvelope envelope, CancellationToken token)
        {
            string id = RequireId(envelope, "workflow profile");
            WorkflowProfile? existing = await _Database.WorkflowProfiles.ReadAsync(id, new WorkflowProfileQuery(), token).ConfigureAwait(false);
            if (existing == null)
            {
                return NotFound("Workflow profile not found.");
            }

            WorkflowProfile incoming = RequireBody<WorkflowProfile>(envelope, "workflow profile");
            existing.Name = incoming.Name;
            existing.Description = incoming.Description;
            existing.Scope = incoming.Scope;
            existing.FleetId = NormalizeEmpty(incoming.FleetId);
            existing.VesselId = NormalizeEmpty(incoming.VesselId);
            existing.IsDefault = incoming.IsDefault;
            existing.Active = incoming.Active;
            existing.LanguageHints = incoming.LanguageHints ?? new List<string>();
            existing.LintCommand = NormalizeEmpty(incoming.LintCommand);
            existing.BuildCommand = NormalizeEmpty(incoming.BuildCommand);
            existing.UnitTestCommand = NormalizeEmpty(incoming.UnitTestCommand);
            existing.IntegrationTestCommand = NormalizeEmpty(incoming.IntegrationTestCommand);
            existing.E2ETestCommand = NormalizeEmpty(incoming.E2ETestCommand);
            existing.MigrationCommand = NormalizeEmpty(incoming.MigrationCommand);
            existing.SecurityScanCommand = NormalizeEmpty(incoming.SecurityScanCommand);
            existing.PerformanceCommand = NormalizeEmpty(incoming.PerformanceCommand);
            existing.PackageCommand = NormalizeEmpty(incoming.PackageCommand);
            existing.DeploymentVerificationCommand = NormalizeEmpty(incoming.DeploymentVerificationCommand);
            existing.RollbackVerificationCommand = NormalizeEmpty(incoming.RollbackVerificationCommand);
            existing.PublishArtifactCommand = NormalizeEmpty(incoming.PublishArtifactCommand);
            existing.ReleaseVersioningCommand = NormalizeEmpty(incoming.ReleaseVersioningCommand);
            existing.ChangelogGenerationCommand = NormalizeEmpty(incoming.ChangelogGenerationCommand);
            existing.RequiredInputs = incoming.RequiredInputs ?? new List<WorkflowInputReference>();
            existing.ExpectedArtifacts = incoming.ExpectedArtifacts ?? new List<string>();
            existing.Environments = incoming.Environments ?? new List<WorkflowEnvironmentProfile>();
            existing.LastUpdateUtc = DateTime.UtcNow;

            WorkflowProfileValidationResult validation = await _WorkflowProfiles.ValidateAsync(existing, token).ConfigureAwait(false);
            if (!validation.IsValid)
            {
                return BadRequest("invalid_workflow_profile", String.Join(" ", validation.Errors));
            }

            await EnsureUniqueDefaultWorkflowProfileAsync(existing, token).ConfigureAwait(false);
            WorkflowProfile updated = await _Database.WorkflowProfiles.UpdateAsync(existing, token).ConfigureAwait(false);
            return Ok(updated, "Workflow profile updated.");
        }

        private async Task<RemoteTunnelRequestResult> DeleteWorkflowProfileAsync(RemoteTunnelEnvelope envelope, CancellationToken token)
        {
            string id = RequireId(envelope, "workflow profile");
            WorkflowProfile? existing = await _Database.WorkflowProfiles.ReadAsync(id, new WorkflowProfileQuery(), token).ConfigureAwait(false);
            if (existing == null)
            {
                return NotFound("Workflow profile not found.");
            }

            await _Database.WorkflowProfiles.DeleteAsync(existing.Id, new WorkflowProfileQuery(), token).ConfigureAwait(false);
            return NoContent("Workflow profile deleted.");
        }

        private async Task<RemoteTunnelRequestResult> ListCheckRunsAsync(RemoteTunnelEnvelope envelope, CancellationToken token)
        {
            CheckRunQuery query = DeserializePayload<CheckRunQuery>(envelope) ?? new CheckRunQuery();
            query.PageNumber = Math.Max(1, query.PageNumber);
            query.PageSize = Math.Clamp(query.PageSize <= 0 ? 25 : query.PageSize, 1, 200);
            return Ok(await _Database.CheckRuns.EnumerateAsync(query, token).ConfigureAwait(false), "Check runs captured.");
        }

        private async Task<RemoteTunnelRequestResult> GetCheckRunDetailAsync(RemoteTunnelEnvelope envelope, CancellationToken token)
        {
            string id = RequireId(envelope, "check run");
            CheckRun? run = await _Database.CheckRuns.ReadAsync(id, new CheckRunQuery(), token).ConfigureAwait(false);
            return run == null ? NotFound("Check run not found.") : Ok(run, "Check run captured.");
        }

        private async Task<RemoteTunnelRequestResult> CreateCheckRunAsync(RemoteTunnelEnvelope envelope, CancellationToken token)
        {
            CheckRunRequest request = RequireBody<CheckRunRequest>(envelope, "check run");
            try
            {
                return Created(await _CheckRuns.RunAsync(_ProxyAuth, request, token).ConfigureAwait(false), "Check run created.");
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest("invalid_check_run", ex.Message);
            }
        }

        private async Task<RemoteTunnelRequestResult> RetryCheckRunAsync(RemoteTunnelEnvelope envelope, CancellationToken token)
        {
            string id = RequireId(envelope, "check run");
            try
            {
                return Created(await _CheckRuns.RetryAsync(_ProxyAuth, id, token).ConfigureAwait(false), "Check run retried.");
            }
            catch (InvalidOperationException ex)
            {
                return NotFound(ex.Message);
            }
        }

        private async Task<RemoteTunnelRequestResult> DeleteCheckRunAsync(RemoteTunnelEnvelope envelope, CancellationToken token)
        {
            string id = RequireId(envelope, "check run");
            CheckRun? existing = await _Database.CheckRuns.ReadAsync(id, new CheckRunQuery(), token).ConfigureAwait(false);
            if (existing == null)
            {
                return NotFound("Check run not found.");
            }

            await _Database.CheckRuns.DeleteAsync(id, new CheckRunQuery(), token).ConfigureAwait(false);
            return NoContent("Check run deleted.");
        }

        private async Task<RemoteTunnelRequestResult> ListEnvironmentsAsync(RemoteTunnelEnvelope envelope, CancellationToken token)
        {
            DeploymentEnvironmentQuery query = DeserializePayload<DeploymentEnvironmentQuery>(envelope) ?? new DeploymentEnvironmentQuery();
            query.PageNumber = Math.Max(1, query.PageNumber);
            query.PageSize = Math.Clamp(query.PageSize <= 0 ? 25 : query.PageSize, 1, 200);
            return Ok(await _Environments.EnumerateAsync(_ProxyAuth, query, token).ConfigureAwait(false), "Environments captured.");
        }

        private async Task<RemoteTunnelRequestResult> GetEnvironmentDetailAsync(RemoteTunnelEnvelope envelope, CancellationToken token)
        {
            string id = RequireId(envelope, "environment");
            DeploymentEnvironment? environment = await _Environments.ReadAsync(_ProxyAuth, id, token).ConfigureAwait(false);
            return environment == null ? NotFound("Environment not found.") : Ok(environment, "Environment captured.");
        }

        private async Task<RemoteTunnelRequestResult> CreateEnvironmentAsync(RemoteTunnelEnvelope envelope, CancellationToken token)
        {
            DeploymentEnvironmentUpsertRequest request = RequireBody<DeploymentEnvironmentUpsertRequest>(envelope, "environment");
            try
            {
                return Created(await _Environments.CreateAsync(_ProxyAuth, request, token).ConfigureAwait(false), "Environment created.");
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest("invalid_environment", ex.Message);
            }
        }

        private async Task<RemoteTunnelRequestResult> UpdateEnvironmentAsync(RemoteTunnelEnvelope envelope, CancellationToken token)
        {
            string id = RequireId(envelope, "environment");
            DeploymentEnvironmentUpsertRequest request = RequireBody<DeploymentEnvironmentUpsertRequest>(envelope, "environment");
            try
            {
                return Ok(await _Environments.UpdateAsync(_ProxyAuth, id, request, token).ConfigureAwait(false), "Environment updated.");
            }
            catch (InvalidOperationException ex)
            {
                return ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase)
                    ? NotFound(ex.Message)
                    : BadRequest("invalid_environment", ex.Message);
            }
        }

        private async Task<RemoteTunnelRequestResult> DeleteEnvironmentAsync(RemoteTunnelEnvelope envelope, CancellationToken token)
        {
            string id = RequireId(envelope, "environment");
            try
            {
                await _Environments.DeleteAsync(_ProxyAuth, id, token).ConfigureAwait(false);
                return NoContent("Environment deleted.");
            }
            catch (InvalidOperationException ex)
            {
                return NotFound(ex.Message);
            }
        }

        private async Task<RemoteTunnelRequestResult> ListReleasesAsync(RemoteTunnelEnvelope envelope, CancellationToken token)
        {
            ReleaseQuery query = DeserializePayload<ReleaseQuery>(envelope) ?? new ReleaseQuery();
            query.PageNumber = Math.Max(1, query.PageNumber);
            query.PageSize = Math.Clamp(query.PageSize <= 0 ? 25 : query.PageSize, 1, 200);
            return Ok(await _Releases.EnumerateAsync(_ProxyAuth, query, token).ConfigureAwait(false), "Releases captured.");
        }

        private async Task<RemoteTunnelRequestResult> GetReleaseDetailAsync(RemoteTunnelEnvelope envelope, CancellationToken token)
        {
            string id = RequireId(envelope, "release");
            Release? release = await _Releases.ReadAsync(_ProxyAuth, id, token).ConfigureAwait(false);
            return release == null ? NotFound("Release not found.") : Ok(release, "Release captured.");
        }

        private async Task<RemoteTunnelRequestResult> CreateReleaseAsync(RemoteTunnelEnvelope envelope, CancellationToken token)
        {
            ReleaseUpsertRequest request = RequireBody<ReleaseUpsertRequest>(envelope, "release");
            try
            {
                await ValidateObjectivesAsync(request.ObjectiveIds, token).ConfigureAwait(false);
                Release release = await _Releases.CreateAsync(_ProxyAuth, request, token).ConfigureAwait(false);
                await LinkReleaseObjectivesAsync(request.ObjectiveIds, release.Id, token).ConfigureAwait(false);
                return Created(release, "Release created.");
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest("invalid_release", ex.Message);
            }
        }

        private async Task<RemoteTunnelRequestResult> UpdateReleaseAsync(RemoteTunnelEnvelope envelope, CancellationToken token)
        {
            string id = RequireId(envelope, "release");
            ReleaseUpsertRequest request = RequireBody<ReleaseUpsertRequest>(envelope, "release");
            try
            {
                await ValidateObjectivesAsync(request.ObjectiveIds, token).ConfigureAwait(false);
                Release release = await _Releases.UpdateAsync(_ProxyAuth, id, request, token).ConfigureAwait(false);
                await LinkReleaseObjectivesAsync(request.ObjectiveIds, release.Id, token).ConfigureAwait(false);
                return Ok(release, "Release updated.");
            }
            catch (InvalidOperationException ex)
            {
                return ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase)
                    ? NotFound(ex.Message)
                    : BadRequest("invalid_release", ex.Message);
            }
        }

        private async Task<RemoteTunnelRequestResult> RefreshReleaseAsync(RemoteTunnelEnvelope envelope, CancellationToken token)
        {
            string id = RequireId(envelope, "release");
            try
            {
                return Ok(await _Releases.RefreshAsync(_ProxyAuth, id, token).ConfigureAwait(false), "Release refreshed.");
            }
            catch (InvalidOperationException ex)
            {
                return ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase)
                    ? NotFound(ex.Message)
                    : BadRequest("invalid_release", ex.Message);
            }
        }

        private async Task<RemoteTunnelRequestResult> DeleteReleaseAsync(RemoteTunnelEnvelope envelope, CancellationToken token)
        {
            string id = RequireId(envelope, "release");
            try
            {
                await _Releases.DeleteAsync(_ProxyAuth, id, token).ConfigureAwait(false);
                return NoContent("Release deleted.");
            }
            catch (InvalidOperationException ex)
            {
                return NotFound(ex.Message);
            }
        }

        private async Task<RemoteTunnelRequestResult> ListDeploymentsAsync(RemoteTunnelEnvelope envelope, CancellationToken token)
        {
            DeploymentQuery query = DeserializePayload<DeploymentQuery>(envelope) ?? new DeploymentQuery();
            query.PageNumber = Math.Max(1, query.PageNumber);
            query.PageSize = Math.Clamp(query.PageSize <= 0 ? 25 : query.PageSize, 1, 200);
            return Ok(await _Deployments.EnumerateAsync(_ProxyAuth, query, token).ConfigureAwait(false), "Deployments captured.");
        }

        private async Task<RemoteTunnelRequestResult> GetDeploymentDetailAsync(RemoteTunnelEnvelope envelope, CancellationToken token)
        {
            string id = RequireId(envelope, "deployment");
            Deployment? deployment = await _Deployments.ReadAsync(_ProxyAuth, id, token).ConfigureAwait(false);
            return deployment == null ? NotFound("Deployment not found.") : Ok(deployment, "Deployment captured.");
        }

        private async Task<RemoteTunnelRequestResult> CreateDeploymentAsync(RemoteTunnelEnvelope envelope, CancellationToken token)
        {
            DeploymentUpsertRequest request = RequireBody<DeploymentUpsertRequest>(envelope, "deployment");
            try
            {
                await ValidateObjectivesAsync(request.ObjectiveIds, token).ConfigureAwait(false);
                Deployment deployment = await _Deployments.CreateAsync(_ProxyAuth, request, token).ConfigureAwait(false);
                await LinkDeploymentObjectivesAsync(deployment, request.ObjectiveIds, token).ConfigureAwait(false);
                return Created(deployment, "Deployment created.");
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest("invalid_deployment", ex.Message);
            }
        }

        private async Task<RemoteTunnelRequestResult> UpdateDeploymentAsync(RemoteTunnelEnvelope envelope, CancellationToken token)
        {
            string id = RequireId(envelope, "deployment");
            DeploymentUpsertRequest request = RequireBody<DeploymentUpsertRequest>(envelope, "deployment");
            try
            {
                await ValidateObjectivesAsync(request.ObjectiveIds, token).ConfigureAwait(false);
                Deployment deployment = await _Deployments.UpdateAsync(_ProxyAuth, id, request, token).ConfigureAwait(false);
                await LinkDeploymentObjectivesAsync(deployment, request.ObjectiveIds, token).ConfigureAwait(false);
                return Ok(deployment, "Deployment updated.");
            }
            catch (InvalidOperationException ex)
            {
                return ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase)
                    ? NotFound(ex.Message)
                    : BadRequest("invalid_deployment", ex.Message);
            }
        }

        private async Task<RemoteTunnelRequestResult> ApproveDeploymentAsync(RemoteTunnelEnvelope envelope, CancellationToken token)
        {
            string id = RequireId(envelope, "deployment");
            string? comment = ReadStringProperty(envelope, "comment");
            try
            {
                return Ok(await _Deployments.ApproveAsync(_ProxyAuth, id, comment, token).ConfigureAwait(false), "Deployment approved.");
            }
            catch (InvalidOperationException ex)
            {
                return ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase)
                    ? NotFound(ex.Message)
                    : BadRequest("invalid_deployment", ex.Message);
            }
        }

        private async Task<RemoteTunnelRequestResult> DenyDeploymentAsync(RemoteTunnelEnvelope envelope, CancellationToken token)
        {
            string id = RequireId(envelope, "deployment");
            string? comment = ReadStringProperty(envelope, "comment");
            try
            {
                return Ok(await _Deployments.DenyAsync(_ProxyAuth, id, comment, token).ConfigureAwait(false), "Deployment denied.");
            }
            catch (InvalidOperationException ex)
            {
                return ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase)
                    ? NotFound(ex.Message)
                    : BadRequest("invalid_deployment", ex.Message);
            }
        }

        private async Task<RemoteTunnelRequestResult> VerifyDeploymentAsync(RemoteTunnelEnvelope envelope, CancellationToken token)
        {
            string id = RequireId(envelope, "deployment");
            try
            {
                return Ok(await _Deployments.VerifyAsync(_ProxyAuth, id, token).ConfigureAwait(false), "Deployment verified.");
            }
            catch (InvalidOperationException ex)
            {
                return ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase)
                    ? NotFound(ex.Message)
                    : BadRequest("invalid_deployment", ex.Message);
            }
        }

        private async Task<RemoteTunnelRequestResult> RollbackDeploymentAsync(RemoteTunnelEnvelope envelope, CancellationToken token)
        {
            string id = RequireId(envelope, "deployment");
            try
            {
                return Ok(await _Deployments.RollbackAsync(_ProxyAuth, id, token).ConfigureAwait(false), "Deployment rolled back.");
            }
            catch (InvalidOperationException ex)
            {
                return ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase)
                    ? NotFound(ex.Message)
                    : BadRequest("invalid_deployment", ex.Message);
            }
        }

        private async Task<RemoteTunnelRequestResult> DeleteDeploymentAsync(RemoteTunnelEnvelope envelope, CancellationToken token)
        {
            string id = RequireId(envelope, "deployment");
            try
            {
                await _Deployments.DeleteAsync(_ProxyAuth, id, token).ConfigureAwait(false);
                return NoContent("Deployment deleted.");
            }
            catch (InvalidOperationException ex)
            {
                return NotFound(ex.Message);
            }
        }

        private async Task<RemoteTunnelRequestResult> ListIncidentsAsync(RemoteTunnelEnvelope envelope, CancellationToken token)
        {
            IncidentQuery query = DeserializePayload<IncidentQuery>(envelope) ?? new IncidentQuery();
            query.PageNumber = Math.Max(1, query.PageNumber);
            query.PageSize = Math.Clamp(query.PageSize <= 0 ? 25 : query.PageSize, 1, 200);
            return Ok(await _Incidents.EnumerateAsync(_ProxyAuth, query, token).ConfigureAwait(false), "Incidents captured.");
        }

        private async Task<RemoteTunnelRequestResult> GetIncidentDetailAsync(RemoteTunnelEnvelope envelope, CancellationToken token)
        {
            string id = RequireId(envelope, "incident");
            Incident? incident = await _Incidents.ReadAsync(_ProxyAuth, id, token).ConfigureAwait(false);
            return incident == null ? NotFound("Incident not found.") : Ok(incident, "Incident captured.");
        }

        private async Task<RemoteTunnelRequestResult> CreateIncidentAsync(RemoteTunnelEnvelope envelope, CancellationToken token)
        {
            IncidentUpsertRequest request = RequireBody<IncidentUpsertRequest>(envelope, "incident");
            try
            {
                await ValidateObjectivesAsync(request.ObjectiveIds, token).ConfigureAwait(false);
                Incident incident = await _Incidents.CreateAsync(_ProxyAuth, request, token).ConfigureAwait(false);
                await LinkIncidentObjectivesAsync(incident, request.ObjectiveIds, token).ConfigureAwait(false);
                return Created(incident, "Incident created.");
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest("invalid_incident", ex.Message);
            }
        }

        private async Task<RemoteTunnelRequestResult> UpdateIncidentAsync(RemoteTunnelEnvelope envelope, CancellationToken token)
        {
            string id = RequireId(envelope, "incident");
            IncidentUpsertRequest request = RequireBody<IncidentUpsertRequest>(envelope, "incident");
            try
            {
                await ValidateObjectivesAsync(request.ObjectiveIds, token).ConfigureAwait(false);
                Incident incident = await _Incidents.UpdateAsync(_ProxyAuth, id, request, token).ConfigureAwait(false);
                await LinkIncidentObjectivesAsync(incident, request.ObjectiveIds, token).ConfigureAwait(false);
                return Ok(incident, "Incident updated.");
            }
            catch (InvalidOperationException ex)
            {
                return ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase)
                    ? NotFound(ex.Message)
                    : BadRequest("invalid_incident", ex.Message);
            }
        }

        private async Task<RemoteTunnelRequestResult> DeleteIncidentAsync(RemoteTunnelEnvelope envelope, CancellationToken token)
        {
            string id = RequireId(envelope, "incident");
            try
            {
                await _Incidents.DeleteAsync(_ProxyAuth, id, token).ConfigureAwait(false);
                return NoContent("Incident deleted.");
            }
            catch (InvalidOperationException ex)
            {
                return NotFound(ex.Message);
            }
        }

        private async Task<RemoteTunnelRequestResult> ListRunbooksAsync(RemoteTunnelEnvelope envelope, CancellationToken token)
        {
            RunbookQuery query = DeserializePayload<RunbookQuery>(envelope) ?? new RunbookQuery();
            query.PageNumber = Math.Max(1, query.PageNumber);
            query.PageSize = Math.Clamp(query.PageSize <= 0 ? 25 : query.PageSize, 1, 200);
            return Ok(await _Runbooks.EnumerateAsync(_ProxyAuth, query, token).ConfigureAwait(false), "Runbooks captured.");
        }

        private async Task<RemoteTunnelRequestResult> GetRunbookDetailAsync(RemoteTunnelEnvelope envelope, CancellationToken token)
        {
            string id = RequireId(envelope, "runbook");
            Runbook? runbook = await _Runbooks.ReadAsync(_ProxyAuth, id, token).ConfigureAwait(false);
            return runbook == null ? NotFound("Runbook not found.") : Ok(runbook, "Runbook captured.");
        }

        private async Task<RemoteTunnelRequestResult> CreateRunbookAsync(RemoteTunnelEnvelope envelope, CancellationToken token)
        {
            RunbookUpsertRequest request = RequireBody<RunbookUpsertRequest>(envelope, "runbook");
            try
            {
                return Created(await _Runbooks.CreateAsync(_ProxyAuth, request, token).ConfigureAwait(false), "Runbook created.");
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest("invalid_runbook", ex.Message);
            }
        }

        private async Task<RemoteTunnelRequestResult> UpdateRunbookAsync(RemoteTunnelEnvelope envelope, CancellationToken token)
        {
            string id = RequireId(envelope, "runbook");
            RunbookUpsertRequest request = RequireBody<RunbookUpsertRequest>(envelope, "runbook");
            try
            {
                return Ok(await _Runbooks.UpdateAsync(_ProxyAuth, id, request, token).ConfigureAwait(false), "Runbook updated.");
            }
            catch (InvalidOperationException ex)
            {
                return ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase)
                    ? NotFound(ex.Message)
                    : BadRequest("invalid_runbook", ex.Message);
            }
        }

        private async Task<RemoteTunnelRequestResult> DeleteRunbookAsync(RemoteTunnelEnvelope envelope, CancellationToken token)
        {
            string id = RequireId(envelope, "runbook");
            try
            {
                await _Runbooks.DeleteAsync(_ProxyAuth, id, token).ConfigureAwait(false);
                return NoContent("Runbook deleted.");
            }
            catch (InvalidOperationException ex)
            {
                return NotFound(ex.Message);
            }
        }

        private async Task<RemoteTunnelRequestResult> ListRunbookExecutionsAsync(RemoteTunnelEnvelope envelope, CancellationToken token)
        {
            RunbookExecutionQuery query = DeserializePayload<RunbookExecutionQuery>(envelope) ?? new RunbookExecutionQuery();
            query.PageNumber = Math.Max(1, query.PageNumber);
            query.PageSize = Math.Clamp(query.PageSize <= 0 ? 25 : query.PageSize, 1, 200);
            return Ok(await _Runbooks.EnumerateExecutionsAsync(_ProxyAuth, query, token).ConfigureAwait(false), "Runbook executions captured.");
        }

        private async Task<RemoteTunnelRequestResult> GetRunbookExecutionDetailAsync(RemoteTunnelEnvelope envelope, CancellationToken token)
        {
            string id = RequireId(envelope, "runbook execution");
            RunbookExecution? execution = await _Runbooks.ReadExecutionAsync(_ProxyAuth, id, token).ConfigureAwait(false);
            return execution == null ? NotFound("Runbook execution not found.") : Ok(execution, "Runbook execution captured.");
        }

        private async Task<RemoteTunnelRequestResult> CreateRunbookExecutionAsync(RemoteTunnelEnvelope envelope, CancellationToken token)
        {
            string id = RequireId(envelope, "runbook");
            RunbookExecutionStartRequest request = DeserializeBodyElement<RunbookExecutionStartRequest>(ReadBodyElement(envelope)) ?? new RunbookExecutionStartRequest();
            try
            {
                return Created(await _Runbooks.StartExecutionAsync(_ProxyAuth, id, request, token).ConfigureAwait(false), "Runbook execution created.");
            }
            catch (InvalidOperationException ex)
            {
                return ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase)
                    ? NotFound(ex.Message)
                    : BadRequest("invalid_runbook_execution", ex.Message);
            }
        }

        private async Task<RemoteTunnelRequestResult> UpdateRunbookExecutionAsync(RemoteTunnelEnvelope envelope, CancellationToken token)
        {
            string id = RequireId(envelope, "runbook execution");
            RunbookExecutionUpdateRequest request = DeserializeBodyElement<RunbookExecutionUpdateRequest>(ReadBodyElement(envelope)) ?? new RunbookExecutionUpdateRequest();
            try
            {
                return Ok(await _Runbooks.UpdateExecutionAsync(_ProxyAuth, id, request, token).ConfigureAwait(false), "Runbook execution updated.");
            }
            catch (InvalidOperationException ex)
            {
                return ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase)
                    ? NotFound(ex.Message)
                    : BadRequest("invalid_runbook_execution", ex.Message);
            }
        }

        private async Task<RemoteTunnelRequestResult> DeleteRunbookExecutionAsync(RemoteTunnelEnvelope envelope, CancellationToken token)
        {
            string id = RequireId(envelope, "runbook execution");
            try
            {
                await _Runbooks.DeleteExecutionAsync(_ProxyAuth, id, token).ConfigureAwait(false);
                return NoContent("Runbook execution deleted.");
            }
            catch (InvalidOperationException ex)
            {
                return NotFound(ex.Message);
            }
        }

        private async Task<RemoteTunnelRequestResult> GetCaptainToolsAsync(RemoteTunnelEnvelope envelope, CancellationToken token)
        {
            string id = RequireId(envelope, "captain");
            Captain? captain = await _Database.Captains.ReadAsync(id, token).ConfigureAwait(false);
            return captain == null
                ? NotFound("Captain not found.")
                : Ok(await _CaptainTools.DescribeAsync(captain).ConfigureAwait(false), "Captain tools captured.");
        }

        private async Task<RemoteTunnelRequestResult> ListRequestHistoryAsync(RemoteTunnelEnvelope envelope, CancellationToken token)
        {
            RequestHistoryQuery query = DeserializePayload<RequestHistoryQuery>(envelope) ?? new RequestHistoryQuery();
            query.PageNumber = Math.Max(1, query.PageNumber);
            query.PageSize = Math.Clamp(query.PageSize <= 0 ? 25 : query.PageSize, 1, 200);
            return Ok(await _Database.RequestHistory.EnumerateAsync(query, token).ConfigureAwait(false), "Request history captured.");
        }

        private async Task<RemoteTunnelRequestResult> GetRequestHistoryDetailAsync(RemoteTunnelEnvelope envelope, CancellationToken token)
        {
            string id = RequireId(envelope, "request history");
            RequestHistoryRecord? record = await _Database.RequestHistory.ReadAsync(id, new RequestHistoryQuery(), token).ConfigureAwait(false);
            return record == null ? NotFound("Request history entry not found.") : Ok(record, "Request history captured.");
        }

        private async Task<RemoteTunnelRequestResult> SummarizeRequestHistoryAsync(RemoteTunnelEnvelope envelope, CancellationToken token)
        {
            RequestHistoryQuery query = DeserializePayload<RequestHistoryQuery>(envelope) ?? new RequestHistoryQuery();
            if (!query.FromUtc.HasValue) query.FromUtc = DateTime.UtcNow.AddHours(-24);
            if (!query.ToUtc.HasValue) query.ToUtc = DateTime.UtcNow;
            if (query.BucketMinutes <= 0) query.BucketMinutes = 15;
            List<RequestHistoryEntry> entries = await _Database.RequestHistory.EnumerateForSummaryAsync(query, token).ConfigureAwait(false);
            return Ok(RequestHistorySummaryBuilder.Build(entries, query), "Request history summary captured.");
        }

        private async Task<RemoteTunnelRequestResult> GetWorkspaceStatusAsync(RemoteTunnelEnvelope envelope, CancellationToken token)
        {
            Vessel vessel = await RequireWorkspaceVesselAsync(envelope, token).ConfigureAwait(false);
            List<WorkspaceActiveMission> activeMissions = await GetActiveMissionSummariesAsync(vessel.Id, token).ConfigureAwait(false);
            return Ok(await _Workspace.GetStatusAsync(vessel, activeMissions).ConfigureAwait(false), "Workspace status captured.");
        }

        private async Task<RemoteTunnelRequestResult> GetWorkspaceTreeAsync(RemoteTunnelEnvelope envelope, CancellationToken token)
        {
            Vessel vessel = await RequireWorkspaceVesselAsync(envelope, token).ConfigureAwait(false);
            string? path = NormalizeEmpty(ReadStringProperty(envelope, "path"));
            return Ok(await _Workspace.GetTreeAsync(vessel, path).ConfigureAwait(false), "Workspace tree captured.");
        }

        private async Task<RemoteTunnelRequestResult> GetWorkspaceFileAsync(RemoteTunnelEnvelope envelope, CancellationToken token)
        {
            Vessel vessel = await RequireWorkspaceVesselAsync(envelope, token).ConfigureAwait(false);
            string? path = NormalizeEmpty(ReadStringProperty(envelope, "path"));
            if (String.IsNullOrWhiteSpace(path))
            {
                return BadRequest("missing_path", "path is required.");
            }

            return Ok(await _Workspace.GetFileAsync(vessel, path).ConfigureAwait(false), "Workspace file captured.");
        }

        private async Task<RemoteTunnelRequestResult> SearchWorkspaceAsync(RemoteTunnelEnvelope envelope, CancellationToken token)
        {
            Vessel vessel = await RequireWorkspaceVesselAsync(envelope, token).ConfigureAwait(false);
            string? query = NormalizeEmpty(ReadStringProperty(envelope, "query"))
                ?? NormalizeEmpty(ReadStringProperty(envelope, "q"));
            if (String.IsNullOrWhiteSpace(query))
            {
                return BadRequest("missing_query", "query is required.");
            }

            int maxResults = ReadIntProperty(envelope, "maxResults") ?? 200;
            maxResults = Math.Clamp(maxResults, 1, 1000);
            return Ok(await _Workspace.SearchAsync(vessel, query, maxResults).ConfigureAwait(false), "Workspace search captured.");
        }

        private async Task<RemoteTunnelRequestResult> GetWorkspaceChangesAsync(RemoteTunnelEnvelope envelope, CancellationToken token)
        {
            Vessel vessel = await RequireWorkspaceVesselAsync(envelope, token).ConfigureAwait(false);
            return Ok(await _Workspace.GetChangesAsync(vessel).ConfigureAwait(false), "Workspace changes captured.");
        }

        private async Task<RemoteTunnelRequestResult> ListPipelinesAsync(RemoteTunnelEnvelope envelope, CancellationToken token)
        {
            EnumerationQuery query = DeserializePayload<EnumerationQuery>(envelope) ?? new EnumerationQuery();
            query.PageNumber = Math.Max(1, query.PageNumber);
            query.PageSize = Math.Clamp(query.PageSize <= 0 ? 25 : query.PageSize, 1, 200);
            return Ok(await _Database.Pipelines.EnumerateAsync(query, token).ConfigureAwait(false), "Pipelines captured.");
        }

        private async Task<RemoteTunnelRequestResult> GetPipelineDetailAsync(RemoteTunnelEnvelope envelope, CancellationToken token)
        {
            string name = RequireId(envelope, "pipeline");
            Pipeline? pipeline = await _Database.Pipelines.ReadByNameAsync(name, token).ConfigureAwait(false);
            return pipeline == null ? NotFound("Pipeline not found.") : Ok(pipeline, "Pipeline captured.");
        }

        private async Task<RemoteTunnelRequestResult> ListPersonasAsync(RemoteTunnelEnvelope envelope, CancellationToken token)
        {
            EnumerationQuery query = DeserializePayload<EnumerationQuery>(envelope) ?? new EnumerationQuery();
            query.PageNumber = Math.Max(1, query.PageNumber);
            query.PageSize = Math.Clamp(query.PageSize <= 0 ? 25 : query.PageSize, 1, 200);
            return Ok(await _Database.Personas.EnumerateAsync(query, token).ConfigureAwait(false), "Personas captured.");
        }

        private async Task<RemoteTunnelRequestResult> GetPersonaDetailAsync(RemoteTunnelEnvelope envelope, CancellationToken token)
        {
            string name = RequireId(envelope, "persona");
            Persona? persona = await _Database.Personas.ReadByNameAsync(name, token).ConfigureAwait(false);
            return persona == null ? NotFound("Persona not found.") : Ok(persona, "Persona captured.");
        }

        private async Task<RemoteTunnelRequestResult> ListPromptTemplatesAsync(RemoteTunnelEnvelope envelope, CancellationToken token)
        {
            EnumerationQuery query = DeserializePayload<EnumerationQuery>(envelope) ?? new EnumerationQuery();
            query.PageNumber = Math.Max(1, query.PageNumber);
            query.PageSize = Math.Clamp(query.PageSize <= 0 ? 25 : query.PageSize, 1, 200);
            return Ok(await _Database.PromptTemplates.EnumerateAsync(query, token).ConfigureAwait(false), "Prompt templates captured.");
        }

        private async Task<RemoteTunnelRequestResult> GetPromptTemplateDetailAsync(RemoteTunnelEnvelope envelope, CancellationToken token)
        {
            string name = RequireId(envelope, "prompt template");
            PromptTemplate? template = await _Database.PromptTemplates.ReadByNameAsync(name, token).ConfigureAwait(false);
            return template == null ? NotFound("Prompt template not found.") : Ok(template, "Prompt template captured.");
        }

        private async Task<ObjectiveRefinementSessionDetail> BuildObjectiveRefinementDetailAsync(string sessionId, CancellationToken token)
        {
            ObjectiveRefinementSession session = await _Database.ObjectiveRefinementSessions.ReadAsync(sessionId, token).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Objective refinement session not found: " + sessionId);
            List<ObjectiveRefinementMessage> messages = await _Database.ObjectiveRefinementMessages.EnumerateBySessionAsync(session.Id, token).ConfigureAwait(false);
            Captain? captain = await _Database.Captains.ReadAsync(session.CaptainId, token).ConfigureAwait(false);
            Vessel? vessel = !String.IsNullOrWhiteSpace(session.VesselId)
                ? await _Database.Vessels.ReadAsync(session.VesselId, token).ConfigureAwait(false)
                : null;
            Objective? objective = await _Objectives.ReadAsync(_ProxyAuth, session.ObjectiveId, token).ConfigureAwait(false);

            return new ObjectiveRefinementSessionDetail
            {
                Session = session,
                Messages = messages.OrderBy(message => message.Sequence).ToList(),
                Captain = captain,
                Vessel = vessel,
                Objective = objective
            };
        }

        private async Task<object> BuildPlanningSessionDetailAsync(string sessionId, CancellationToken token)
        {
            PlanningSession session = await _Database.PlanningSessions.ReadAsync(sessionId, token).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Planning session not found: " + sessionId);
            List<PlanningSessionMessage> messages = await _Database.PlanningSessionMessages.EnumerateBySessionAsync(session.Id, token).ConfigureAwait(false);
            Captain? captain = await _Database.Captains.ReadAsync(session.CaptainId, token).ConfigureAwait(false);
            Vessel? vessel = await _Database.Vessels.ReadAsync(session.VesselId, token).ConfigureAwait(false);
            return new
            {
                Session = session,
                Messages = messages.OrderBy(message => message.Sequence).ToList(),
                Captain = captain,
                Vessel = vessel
            };
        }

        private async Task EnsureUniqueDefaultWorkflowProfileAsync(WorkflowProfile profile, CancellationToken token)
        {
            if (!profile.IsDefault)
            {
                return;
            }

            WorkflowProfileQuery query = new WorkflowProfileQuery
            {
                TenantId = profile.TenantId,
                Scope = profile.Scope,
                PageNumber = 1,
                PageSize = 1000
            };

            if (profile.Scope == WorkflowProfileScopeEnum.Fleet)
                query.FleetId = profile.FleetId;
            if (profile.Scope == WorkflowProfileScopeEnum.Vessel)
                query.VesselId = profile.VesselId;

            List<WorkflowProfile> peers = await _Database.WorkflowProfiles.EnumerateAllAsync(query, token).ConfigureAwait(false);
            foreach (WorkflowProfile peer in peers.Where(item => item.IsDefault && !String.Equals(item.Id, profile.Id, StringComparison.Ordinal)))
            {
                peer.IsDefault = false;
                await _Database.WorkflowProfiles.UpdateAsync(peer, token).ConfigureAwait(false);
            }
        }

        private async Task ValidateObjectivesAsync(IEnumerable<string>? objectiveIds, CancellationToken token)
        {
            if (objectiveIds == null) return;

            foreach (string objectiveId in objectiveIds)
            {
                string? normalized = NormalizeEmpty(objectiveId);
                if (String.IsNullOrWhiteSpace(normalized))
                    continue;

                Objective? objective = await _Objectives.ReadAsync(_ProxyAuth, normalized, token).ConfigureAwait(false);
                if (objective == null)
                    throw new InvalidOperationException("Objective not found: " + normalized);
            }
        }

        private async Task LinkReleaseObjectivesAsync(IEnumerable<string>? objectiveIds, string releaseId, CancellationToken token)
        {
            if (objectiveIds == null) return;

            foreach (string objectiveId in objectiveIds)
            {
                string? normalized = NormalizeEmpty(objectiveId);
                if (String.IsNullOrWhiteSpace(normalized))
                    continue;

                await _Objectives.LinkReleaseAsync(_ProxyAuth, normalized, releaseId, token).ConfigureAwait(false);
            }
        }

        private async Task LinkDeploymentObjectivesAsync(Deployment deployment, IEnumerable<string>? explicitObjectiveIds, CancellationToken token)
        {
            HashSet<string> objectiveIds = await ResolveObjectiveIdsAsync(
                explicitObjectiveIds,
                deployment.ReleaseId,
                deployment.MissionId,
                deployment.VoyageId,
                deployment.Id,
                token).ConfigureAwait(false);

            foreach (string objectiveId in objectiveIds)
            {
                await _Objectives.LinkDeploymentAsync(_ProxyAuth, objectiveId, deployment.Id, token).ConfigureAwait(false);
            }
        }

        private async Task LinkIncidentObjectivesAsync(Incident incident, IEnumerable<string>? explicitObjectiveIds, CancellationToken token)
        {
            HashSet<string> objectiveIds = await ResolveObjectiveIdsAsync(
                explicitObjectiveIds,
                incident.ReleaseId,
                incident.MissionId,
                incident.VoyageId,
                incident.DeploymentId,
                token,
                incident.RollbackDeploymentId).ConfigureAwait(false);

            foreach (string objectiveId in objectiveIds)
            {
                await _Objectives.LinkIncidentAsync(_ProxyAuth, objectiveId, incident.Id, token).ConfigureAwait(false);
            }
        }

        private async Task<HashSet<string>> ResolveObjectiveIdsAsync(
            IEnumerable<string>? explicitObjectiveIds,
            string? releaseId,
            string? missionId,
            string? voyageId,
            string? deploymentId,
            CancellationToken token,
            string? secondaryDeploymentId = null)
        {
            HashSet<string> objectiveIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (explicitObjectiveIds != null)
            {
                foreach (string objectiveId in explicitObjectiveIds)
                {
                    string? normalized = NormalizeEmpty(objectiveId);
                    if (!String.IsNullOrWhiteSpace(normalized))
                        objectiveIds.Add(normalized);
                }
            }

            await AddObjectivesForQueryAsync(objectiveIds, query => query.ReleaseId = NormalizeEmpty(releaseId), token).ConfigureAwait(false);
            await AddObjectivesForQueryAsync(objectiveIds, query => query.MissionId = NormalizeEmpty(missionId), token).ConfigureAwait(false);
            await AddObjectivesForQueryAsync(objectiveIds, query => query.VoyageId = NormalizeEmpty(voyageId), token).ConfigureAwait(false);
            await AddObjectivesForQueryAsync(objectiveIds, query => query.DeploymentId = NormalizeEmpty(deploymentId), token).ConfigureAwait(false);
            await AddObjectivesForQueryAsync(objectiveIds, query => query.DeploymentId = NormalizeEmpty(secondaryDeploymentId), token).ConfigureAwait(false);
            return objectiveIds;
        }

        private async Task AddObjectivesForQueryAsync(
            HashSet<string> objectiveIds,
            Action<ObjectiveQuery> configure,
            CancellationToken token)
        {
            ObjectiveQuery query = new ObjectiveQuery
            {
                PageNumber = 1,
                PageSize = 200
            };
            configure(query);

            if (String.IsNullOrWhiteSpace(query.ReleaseId)
                && String.IsNullOrWhiteSpace(query.MissionId)
                && String.IsNullOrWhiteSpace(query.VoyageId)
                && String.IsNullOrWhiteSpace(query.DeploymentId))
            {
                return;
            }

            while (true)
            {
                EnumerationResult<Objective> results = await _Objectives.EnumerateAsync(_ProxyAuth, query, token).ConfigureAwait(false);
                foreach (Objective objective in results.Objects)
                    objectiveIds.Add(objective.Id);

                if (results.PageNumber >= results.TotalPages || results.Objects.Count == 0)
                    return;

                query.PageNumber++;
            }
        }

        private async Task<Vessel> RequireWorkspaceVesselAsync(RemoteTunnelEnvelope envelope, CancellationToken token)
        {
            string vesselId = RequireId(envelope, "vessel");
            Vessel? vessel = await _Database.Vessels.ReadAsync(vesselId, token).ConfigureAwait(false);
            if (vessel == null)
            {
                throw new InvalidOperationException("Vessel not found.");
            }

            return vessel;
        }

        private async Task<List<WorkspaceActiveMission>> GetActiveMissionSummariesAsync(string vesselId, CancellationToken token)
        {
            List<Mission> missions = await _Database.Missions.EnumerateByVesselAsync(vesselId, token).ConfigureAwait(false);
            return missions
                .Where(IsActiveMission)
                .OrderByDescending(mission => mission.LastUpdateUtc)
                .Select(mission => new WorkspaceActiveMission
                {
                    MissionId = mission.Id,
                    Title = mission.Title ?? String.Empty,
                    Status = mission.Status.ToString(),
                    ScopedFiles = ParseMissionScopedFiles(mission.Description ?? String.Empty).ToList()
                })
                .ToList();
        }

        private static bool IsActiveMission(Mission mission)
        {
            return mission.Status == MissionStatusEnum.Pending
                || mission.Status == MissionStatusEnum.Assigned
                || mission.Status == MissionStatusEnum.InProgress
                || mission.Status == MissionStatusEnum.Review
                || mission.Status == MissionStatusEnum.Testing;
        }

        private static HashSet<string> ParseMissionScopedFiles(string description)
        {
            HashSet<string> files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (String.IsNullOrWhiteSpace(description))
                return files;

            string[] lines = description.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            foreach (string line in lines)
            {
                string trimmed = line.Trim();
                if (!trimmed.StartsWith("Touch only ", StringComparison.OrdinalIgnoreCase)
                    && !trimmed.StartsWith("Edit only ", StringComparison.OrdinalIgnoreCase)
                    && !trimmed.StartsWith("Modify only ", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                int marker = trimmed.IndexOf(" only ", StringComparison.OrdinalIgnoreCase);
                if (marker < 0 || marker + 6 >= trimmed.Length)
                    continue;

                string fileSegment = trimmed.Substring(marker + 6);
                foreach (string token in fileSegment.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string normalized = token.Trim().Replace('\\', '/');
                    if (!String.IsNullOrWhiteSpace(normalized))
                        files.Add(normalized);
                }
            }

            return files;
        }

        private static string RequireId(RemoteTunnelEnvelope envelope, string label)
        {
            string? id = NormalizeEmpty(ReadStringProperty(envelope, "id"))
                ?? NormalizeEmpty(ReadStringProperty(envelope, label + "Id"));
            if (String.IsNullOrWhiteSpace(id))
            {
                throw new InvalidOperationException(label + " id is required.");
            }

            return id;
        }

        private static T RequireBody<T>(RemoteTunnelEnvelope envelope, string label)
        {
            JsonElement? body = ReadBodyElement(envelope);
            T? request = DeserializeBodyElement<T>(body);
            if (request == null)
            {
                throw new InvalidOperationException(label + " payload is required.");
            }

            return request;
        }

        private static T? DeserializePayload<T>(RemoteTunnelEnvelope envelope)
        {
            if (!envelope.Payload.HasValue)
            {
                return default;
            }

            try
            {
                return envelope.Payload.Value.Deserialize<T>(_BodyJsonOptions);
            }
            catch (JsonException)
            {
                return default;
            }
        }

        private static T? DeserializeBodyElement<T>(JsonElement? body)
        {
            if (!body.HasValue)
            {
                return default;
            }

            try
            {
                return body.Value.Deserialize<T>(_BodyJsonOptions);
            }
            catch (JsonException)
            {
                return default;
            }
        }

        private static JsonElement? ReadBodyElement(RemoteTunnelEnvelope envelope)
        {
            if (!envelope.Payload.HasValue || envelope.Payload.Value.ValueKind != JsonValueKind.Object)
            {
                return envelope.Payload;
            }

            foreach (JsonProperty property in envelope.Payload.Value.EnumerateObject())
            {
                if (String.Equals(property.Name, "body", StringComparison.OrdinalIgnoreCase))
                {
                    return property.Value.Clone();
                }
            }

            return envelope.Payload;
        }

        private static string? ReadStringProperty(RemoteTunnelEnvelope envelope, string propertyName)
        {
            if (!envelope.Payload.HasValue || envelope.Payload.Value.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            foreach (JsonProperty property in envelope.Payload.Value.EnumerateObject())
            {
                if (String.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    return property.Value.ValueKind == JsonValueKind.Null ? null : property.Value.ToString();
                }
            }

            return null;
        }

        private static int? ReadIntProperty(RemoteTunnelEnvelope envelope, string propertyName)
        {
            string? value = ReadStringProperty(envelope, propertyName);
            return Int32.TryParse(value, out int parsed) ? parsed : null;
        }

        private static string? NormalizeEmpty(string? value)
        {
            return String.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        private static RemoteTunnelRequestResult Ok(object payload, string message)
        {
            return new RemoteTunnelRequestResult
            {
                StatusCode = 200,
                Payload = payload,
                Message = message
            };
        }

        private static RemoteTunnelRequestResult Created(object payload, string message)
        {
            return new RemoteTunnelRequestResult
            {
                StatusCode = 201,
                Payload = payload,
                Message = message
            };
        }

        private static RemoteTunnelRequestResult NoContent(string message)
        {
            return new RemoteTunnelRequestResult
            {
                StatusCode = 204,
                Message = message
            };
        }

        private static RemoteTunnelRequestResult BadRequest(string errorCode, string message)
        {
            return new RemoteTunnelRequestResult
            {
                StatusCode = 400,
                ErrorCode = errorCode,
                Message = message
            };
        }

        private static RemoteTunnelRequestResult Conflict(string message)
        {
            return new RemoteTunnelRequestResult
            {
                StatusCode = 409,
                ErrorCode = "conflict",
                Message = message
            };
        }

        private static RemoteTunnelRequestResult NotFound(string message)
        {
            return new RemoteTunnelRequestResult
            {
                StatusCode = 404,
                ErrorCode = "not_found",
                Message = message
            };
        }

        private static RemoteTunnelRequestResult Error(int statusCode, string errorCode, string message)
        {
            return new RemoteTunnelRequestResult
            {
                StatusCode = statusCode,
                ErrorCode = errorCode,
                Message = message
            };
        }

        private static RemoteTunnelRequestResult Unsupported(string? method)
        {
            return new RemoteTunnelRequestResult
            {
                StatusCode = 404,
                ErrorCode = "unsupported_method",
                Message = "Unsupported tunnel method " + method + "."
            };
        }

        private sealed class PlanningSessionListRequest
        {
            public string? ObjectiveId { get; set; } = null;
            public string? CaptainId { get; set; } = null;
            public string? VesselId { get; set; } = null;
            public string? Status { get; set; } = null;
            public int Limit { get; set; } = 0;
        }
    }
}
