namespace Armada.Test.Unit.Suites.Services
{
    using System.Text.Json;
    using Armada.Core;
    using Armada.Core.Database;
    using Armada.Core.Database.Sqlite;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Runtimes;
    using Armada.Server;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    public class RemoteControlOperationsServiceTests : TestSuite
    {
        public override string Name => "Remote Control Operations Service";

        protected override async Task RunTestsAsync()
        {
            await RunTest("BacklogRefinementPlanningAndDispatchOperateThroughTunnel", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                using OperationsFixture fixture = new OperationsFixture(testDb.Driver);

                await fixture.EnsureDefaultPrincipalAsync().ConfigureAwait(false);
                Vessel vessel = await fixture.CreateVesselAsync("proxy-phase1").ConfigureAwait(false);
                Captain refinementCaptain = await fixture.CreateCaptainAsync("proxy-refiner", AgentRuntimeEnum.ClaudeCode).ConfigureAwait(false);
                Captain planningCaptain = await fixture.CreateCaptainAsync("proxy-planner", AgentRuntimeEnum.ClaudeCode).ConfigureAwait(false);
                Pipeline pipeline = await fixture.CreatePipelineAsync("Proxy Planning Pipeline").ConfigureAwait(false);

                RemoteTunnelRequestResult createObjective = await fixture.Service.HandleAsync(
                    RemoteTunnelProtocol.CreateRequest("armada.backlog.create", new
                    {
                        body = new ObjectiveUpsertRequest
                        {
                            Title = "Proxy backlog objective",
                            Description = "Create the proxy orchestration slice.",
                            Status = ObjectiveStatusEnum.Draft,
                            BacklogState = ObjectiveBacklogStateEnum.Inbox,
                            VesselIds = new List<string> { vessel.Id },
                            FleetIds = new List<string> { vessel.FleetId! },
                            Tags = new List<string> { "proxy", "phase1" }
                        }
                    }),
                    CancellationToken.None).ConfigureAwait(false);

                AssertEqual(201, createObjective.StatusCode, "create objective");
                Objective createdObjective = DeserializePayload<Objective>(createObjective);
                AssertEqual("Proxy backlog objective", createdObjective.Title);

                RemoteTunnelRequestResult listObjectives = await fixture.Service.HandleAsync(
                    RemoteTunnelProtocol.CreateRequest("armada.backlog.list", new ObjectiveQuery
                    {
                        PageNumber = 1,
                        PageSize = 10,
                        Search = "Proxy backlog objective"
                    }),
                    CancellationToken.None).ConfigureAwait(false);

                AssertEqual(200, listObjectives.StatusCode, "list objectives");
                AssertContains("Proxy backlog objective", SerializePayload(listObjectives));

                RemoteTunnelRequestResult createRefinement = await fixture.Service.HandleAsync(
                    RemoteTunnelProtocol.CreateRequest("armada.objective-refinement-sessions.create", new
                    {
                        objectiveId = createdObjective.Id,
                        body = new ObjectiveRefinementSessionCreateRequest
                        {
                            CaptainId = refinementCaptain.Id,
                            FleetId = vessel.FleetId,
                            VesselId = vessel.Id,
                            Title = "Refine proxy backlog objective"
                        }
                    }),
                    CancellationToken.None).ConfigureAwait(false);

                AssertEqual(201, createRefinement.StatusCode, "create refinement");
                ObjectiveRefinementSessionDetail refinementDetail = DeserializePayload<ObjectiveRefinementSessionDetail>(createRefinement);
                AssertEqual(createdObjective.Id, refinementDetail.Session.ObjectiveId);
                AssertEqual(refinementCaptain.Id, refinementDetail.Session.CaptainId);

                RemoteTunnelRequestResult createPlanning = await fixture.Service.HandleAsync(
                    RemoteTunnelProtocol.CreateRequest("armada.planning-session.create", new
                    {
                        body = new PlanningSessionCreateRequest
                        {
                            Title = "Plan proxy backlog objective",
                            CaptainId = planningCaptain.Id,
                            VesselId = vessel.Id,
                            FleetId = vessel.FleetId,
                            PipelineId = pipeline.Id,
                            ObjectiveId = createdObjective.Id
                        }
                    }),
                    CancellationToken.None).ConfigureAwait(false);

                AssertEqual(201, createPlanning.StatusCode, "create planning session");
                JsonDocument planningDocument = ParsePayload(createPlanning);
                string planningSessionId = planningDocument.RootElement.GetProperty("session").GetProperty("id").GetString()
                    ?? throw new InvalidOperationException("Planning session id missing.");

                await fixture.Database.PlanningSessionMessages.CreateAsync(new PlanningSessionMessage
                {
                    PlanningSessionId = planningSessionId,
                    Role = "Assistant",
                    Sequence = 1,
                    Content = "Dispatch a planning-backed voyage for the proxy backlog objective."
                }).ConfigureAwait(false);

                RemoteTunnelRequestResult dispatchPlanning = await fixture.Service.HandleAsync(
                    RemoteTunnelProtocol.CreateRequest("armada.planning-session.dispatch", new
                    {
                        id = planningSessionId,
                        body = new PlanningSessionDispatchRequest
                        {
                            Title = "Proxy backlog dispatch"
                        }
                    }),
                    CancellationToken.None).ConfigureAwait(false);

                AssertEqual(200, dispatchPlanning.StatusCode, "dispatch planning session");
                Voyage dispatchedVoyage = DeserializePayload<Voyage>(dispatchPlanning);
                AssertEqual(planningSessionId, dispatchedVoyage.SourcePlanningSessionId);
                AssertEqual("Proxy backlog dispatch", dispatchedVoyage.Title);

                Objective persistedObjective = await fixture.RequireObjectiveAsync(createdObjective.Id).ConfigureAwait(false);
                AssertTrue(persistedObjective.PlanningSessionIds.Contains(planningSessionId), "Objective should link to the proxy-created planning session.");
                AssertTrue(persistedObjective.VoyageIds.Contains(dispatchedVoyage.Id), "Objective should link to the dispatched voyage.");
            }).ConfigureAwait(false);

            await RunTest("WorkflowDeliveryIncidentAndRunbookFlowsOperateThroughTunnel", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                using OperationsFixture fixture = new OperationsFixture(testDb.Driver);

                await fixture.EnsureDefaultPrincipalAsync().ConfigureAwait(false);
                Vessel vessel = await fixture.CreateVesselAsync("proxy-phase2", initializeRepository: true).ConfigureAwait(false);
                Objective objective = await fixture.CreateObjectiveAsync("Phase 2 objective", vessel).ConfigureAwait(false);

                RemoteTunnelRequestResult createWorkflowProfile = await fixture.Service.HandleAsync(
                    RemoteTunnelProtocol.CreateRequest("armada.workflow-profile.create", new
                    {
                        body = new WorkflowProfile
                        {
                            Name = "Proxy Vessel Workflow",
                            Scope = WorkflowProfileScopeEnum.Vessel,
                            VesselId = vessel.Id,
                            BuildCommand = "dotnet --version",
                            Environments = new List<WorkflowEnvironmentProfile>
                            {
                                new WorkflowEnvironmentProfile
                                {
                                    EnvironmentName = "staging",
                                    DeployCommand = "echo deploy staging"
                                }
                            }
                        }
                    }),
                    CancellationToken.None).ConfigureAwait(false);

                AssertEqual(201, createWorkflowProfile.StatusCode, "create workflow profile");
                WorkflowProfile workflowProfile = DeserializePayload<WorkflowProfile>(createWorkflowProfile);

                RemoteTunnelRequestResult createCheckRun = await fixture.Service.HandleAsync(
                    RemoteTunnelProtocol.CreateRequest("armada.check-run.create", new
                    {
                        body = new CheckRunRequest
                        {
                            VesselId = vessel.Id,
                            WorkflowProfileId = workflowProfile.Id,
                            Type = CheckRunTypeEnum.Build,
                            Label = "Proxy build check"
                        }
                    }),
                    CancellationToken.None).ConfigureAwait(false);

                AssertEqual(201, createCheckRun.StatusCode, "create check run");
                CheckRun checkRun = DeserializePayload<CheckRun>(createCheckRun);
                AssertEqual(CheckRunStatusEnum.Passed, checkRun.Status);

                RemoteTunnelRequestResult createEnvironment = await fixture.Service.HandleAsync(
                    RemoteTunnelProtocol.CreateRequest("armada.environment.create", new
                    {
                        body = new DeploymentEnvironmentUpsertRequest
                        {
                            VesselId = vessel.Id,
                            Name = "staging",
                            Description = "Proxy staging environment",
                            BaseUrl = "https://staging.example.test",
                            HealthEndpoint = "/health",
                            RequiresApproval = true,
                            IsDefault = true,
                            Active = true
                        }
                    }),
                    CancellationToken.None).ConfigureAwait(false);

                AssertEqual(201, createEnvironment.StatusCode, "create environment");
                DeploymentEnvironment environment = DeserializePayload<DeploymentEnvironment>(createEnvironment);

                RemoteTunnelRequestResult createRelease = await fixture.Service.HandleAsync(
                    RemoteTunnelProtocol.CreateRequest("armada.release.create", new
                    {
                        body = new ReleaseUpsertRequest
                        {
                            VesselId = vessel.Id,
                            WorkflowProfileId = workflowProfile.Id,
                            Title = "Proxy Staging Release",
                            Version = "1.2.3",
                            Status = ReleaseStatusEnum.Draft,
                            CheckRunIds = new List<string> { checkRun.Id },
                            ObjectiveIds = new List<string> { objective.Id }
                        }
                    }),
                    CancellationToken.None).ConfigureAwait(false);

                AssertEqual(201, createRelease.StatusCode, "create release");
                Release release = DeserializePayload<Release>(createRelease);

                RemoteTunnelRequestResult createDeployment = await fixture.Service.HandleAsync(
                    RemoteTunnelProtocol.CreateRequest("armada.deployment.create", new
                    {
                        body = new DeploymentUpsertRequest
                        {
                            VesselId = vessel.Id,
                            WorkflowProfileId = workflowProfile.Id,
                            EnvironmentId = environment.Id,
                            ReleaseId = release.Id,
                            ObjectiveIds = new List<string> { objective.Id },
                            Title = "Proxy deployment",
                            SourceRef = "refs/tags/v1.2.3",
                            AutoExecute = false
                        }
                    }),
                    CancellationToken.None).ConfigureAwait(false);

                AssertEqual(201, createDeployment.StatusCode, "create deployment");
                Deployment deployment = DeserializePayload<Deployment>(createDeployment);
                AssertEqual(DeploymentStatusEnum.PendingApproval, deployment.Status);

                RemoteTunnelRequestResult denyDeployment = await fixture.Service.HandleAsync(
                    RemoteTunnelProtocol.CreateRequest("armada.deployment.deny", new
                    {
                        deploymentId = deployment.Id,
                        comment = "Hold until next release window."
                    }),
                    CancellationToken.None).ConfigureAwait(false);

                AssertEqual(200, denyDeployment.StatusCode, "deny deployment");
                Deployment deniedDeployment = DeserializePayload<Deployment>(denyDeployment);
                AssertEqual(DeploymentStatusEnum.Denied, deniedDeployment.Status);

                RemoteTunnelRequestResult createIncident = await fixture.Service.HandleAsync(
                    RemoteTunnelProtocol.CreateRequest("armada.incident.create", new
                    {
                        body = new IncidentUpsertRequest
                        {
                            Title = "Proxy release regression",
                            Summary = "Staging reported an unhealthy deployment.",
                            Severity = IncidentSeverityEnum.High,
                            EnvironmentId = environment.Id,
                            DeploymentId = deployment.Id,
                            ReleaseId = release.Id,
                            VesselId = vessel.Id,
                            ObjectiveIds = new List<string> { objective.Id }
                        }
                    }),
                    CancellationToken.None).ConfigureAwait(false);

                AssertEqual(201, createIncident.StatusCode, "create incident");
                Incident incident = DeserializePayload<Incident>(createIncident);
                AssertEqual(deployment.Id, incident.DeploymentId);

                RemoteTunnelRequestResult createRunbook = await fixture.Service.HandleAsync(
                    RemoteTunnelProtocol.CreateRequest("armada.runbook.create", new
                    {
                        body = new RunbookUpsertRequest
                        {
                            FileName = "STAGING_RECOVERY.md",
                            Title = "Staging Recovery",
                            Description = "Recover staging after a failed rollout.",
                            WorkflowProfileId = workflowProfile.Id,
                            EnvironmentId = environment.Id,
                            DefaultCheckType = CheckRunTypeEnum.Deploy,
                            OverviewMarkdown = "## Verify\nConfirm service health.\n\n## Recover\nRollback if required.",
                            Steps = new List<RunbookStep>
                            {
                                new RunbookStep
                                {
                                    Id = "verify",
                                    Title = "Verify",
                                    Instructions = "Confirm service health."
                                },
                                new RunbookStep
                                {
                                    Id = "rollback",
                                    Title = "Rollback",
                                    Instructions = "Rollback if required."
                                }
                            }
                        }
                    }),
                    CancellationToken.None).ConfigureAwait(false);

                AssertEqual(201, createRunbook.StatusCode, "create runbook");
                Runbook runbook = DeserializePayload<Runbook>(createRunbook);

                RemoteTunnelRequestResult startExecution = await fixture.Service.HandleAsync(
                    RemoteTunnelProtocol.CreateRequest("armada.runbook-execution.create", new
                    {
                        runbookId = runbook.Id,
                        body = new RunbookExecutionStartRequest
                        {
                            Title = "Proxy staging recovery execution",
                            DeploymentId = deployment.Id,
                            IncidentId = incident.Id,
                            Notes = "Run through rollback checklist."
                        }
                    }),
                    CancellationToken.None).ConfigureAwait(false);

                AssertEqual(201, startExecution.StatusCode, "start runbook execution");
                RunbookExecution execution = DeserializePayload<RunbookExecution>(startExecution);

                RemoteTunnelRequestResult updateExecution = await fixture.Service.HandleAsync(
                    RemoteTunnelProtocol.CreateRequest("armada.runbook-execution.update", new
                    {
                        id = execution.Id,
                        body = new RunbookExecutionUpdateRequest
                        {
                            Status = RunbookExecutionStatusEnum.Completed,
                            CompletedStepIds = new List<string> { "verify", "rollback" },
                            Notes = "Recovery completed."
                        }
                    }),
                    CancellationToken.None).ConfigureAwait(false);

                AssertEqual(200, updateExecution.StatusCode, "update runbook execution");
                RunbookExecution completedExecution = DeserializePayload<RunbookExecution>(updateExecution);
                AssertEqual(RunbookExecutionStatusEnum.Completed, completedExecution.Status);
                AssertEqual(2, completedExecution.CompletedStepIds.Count);

                Objective persistedObjective = await fixture.RequireObjectiveAsync(objective.Id).ConfigureAwait(false);
                AssertTrue(persistedObjective.ReleaseIds.Contains(release.Id), "Objective should link to created release.");
                AssertTrue(persistedObjective.DeploymentIds.Contains(deployment.Id), "Objective should link to created deployment.");
                AssertTrue(persistedObjective.IncidentIds.Contains(incident.Id), "Objective should link to created incident.");
            }).ConfigureAwait(false);

            await RunTest("DiagnosticsFlowsOperateThroughTunnel", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                using OperationsFixture fixture = new OperationsFixture(testDb.Driver);

                await fixture.EnsureDefaultPrincipalAsync().ConfigureAwait(false);
                Captain customCaptain = await fixture.CreateCaptainAsync("proxy-diagnostics", AgentRuntimeEnum.Custom).ConfigureAwait(false);

                RequestHistoryEntry entry = new RequestHistoryEntry
                {
                    TenantId = Constants.DefaultTenantId,
                    UserId = Constants.DefaultUserId,
                    Method = "GET",
                    Route = "/api/v1/proxy/test",
                    StatusCode = 200,
                    DurationMs = 123,
                    RequestContentType = "application/json",
                    ResponseContentType = "application/json",
                    IsSuccess = true,
                    CreatedUtc = DateTime.UtcNow.AddMinutes(-5)
                };
                RequestHistoryDetail detail = new RequestHistoryDetail
                {
                    RequestHistoryId = entry.Id,
                    RequestBodyText = "{\"query\":\"proxy\"}",
                    ResponseBodyText = "{\"ok\":true}"
                };
                await fixture.Database.RequestHistory.CreateAsync(entry, detail).ConfigureAwait(false);

                RemoteTunnelRequestResult captainTools = await fixture.Service.HandleAsync(
                    RemoteTunnelProtocol.CreateRequest("armada.captain.tools", new
                    {
                        captainId = customCaptain.Id
                    }),
                    CancellationToken.None).ConfigureAwait(false);

                AssertEqual(200, captainTools.StatusCode, "captain tools");
                CaptainToolAccessResult toolAccess = DeserializePayload<CaptainToolAccessResult>(captainTools);
                AssertEqual(customCaptain.Id, toolAccess.CaptainId);
                AssertFalse(toolAccess.ToolsAccessible);
                AssertContains("unsupported", toolAccess.AvailabilitySource ?? String.Empty);

                RemoteTunnelRequestResult requestHistoryList = await fixture.Service.HandleAsync(
                    RemoteTunnelProtocol.CreateRequest("armada.request-history.list", new RequestHistoryQuery
                    {
                        PageNumber = 1,
                        PageSize = 10,
                        Route = "/api/v1/proxy/test"
                    }),
                    CancellationToken.None).ConfigureAwait(false);

                AssertEqual(200, requestHistoryList.StatusCode, "request history list");
                AssertContains("/api/v1/proxy/test", SerializePayload(requestHistoryList));

                RemoteTunnelRequestResult requestHistoryDetail = await fixture.Service.HandleAsync(
                    RemoteTunnelProtocol.CreateRequest("armada.request-history.detail", new
                    {
                        id = entry.Id
                    }),
                    CancellationToken.None).ConfigureAwait(false);

                AssertEqual(200, requestHistoryDetail.StatusCode, "request history detail");
                RequestHistoryRecord requestRecord = DeserializePayload<RequestHistoryRecord>(requestHistoryDetail);
                AssertEqual("{\"ok\":true}", requestRecord.Detail?.ResponseBodyText, "request history response body");

                RemoteTunnelRequestResult requestHistorySummary = await fixture.Service.HandleAsync(
                    RemoteTunnelProtocol.CreateRequest("armada.request-history.summary", new RequestHistoryQuery
                    {
                        FromUtc = DateTime.UtcNow.AddHours(-1),
                        ToUtc = DateTime.UtcNow,
                        BucketMinutes = 15
                    }),
                    CancellationToken.None).ConfigureAwait(false);

                AssertEqual(200, requestHistorySummary.StatusCode, "request history summary");
                AssertContains("totalCount", SerializePayload(requestHistorySummary));
            }).ConfigureAwait(false);

            await RunTest("WorkspaceAndReferenceViewsOperateThroughTunnel", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                using OperationsFixture fixture = new OperationsFixture(testDb.Driver);

                await fixture.EnsureDefaultPrincipalAsync().ConfigureAwait(false);
                await fixture.PromptTemplates.SeedDefaultsAsync().ConfigureAwait(false);

                Vessel vessel = await fixture.CreateVesselAsync("proxy-phase4", initializeRepository: true).ConfigureAwait(false);
                await File.WriteAllTextAsync(Path.Combine(vessel.WorkingDirectory!, "notes.txt"), "Proxy workspace token").ConfigureAwait(false);
                Directory.CreateDirectory(Path.Combine(vessel.WorkingDirectory!, "docs"));
                await File.WriteAllTextAsync(Path.Combine(vessel.WorkingDirectory!, "docs", "guide.md"), "# Guide").ConfigureAwait(false);

                Pipeline pipeline = await fixture.CreatePipelineAsync("Proxy Reference Pipeline").ConfigureAwait(false);
                Persona persona = new Persona("Proxy Analyst", "persona.worker")
                {
                    Description = "Proxy-only reference persona.",
                    Active = true,
                    IsBuiltIn = false
                };
                await fixture.Database.Personas.CreateAsync(persona).ConfigureAwait(false);

                RemoteTunnelRequestResult workspaceStatus = await fixture.Service.HandleAsync(
                    RemoteTunnelProtocol.CreateRequest("armada.workspace.status", new
                    {
                        vesselId = vessel.Id
                    }),
                    CancellationToken.None).ConfigureAwait(false);

                AssertEqual(200, workspaceStatus.StatusCode, "workspace status");
                WorkspaceStatusResult statusResult = DeserializePayload<WorkspaceStatusResult>(workspaceStatus);
                AssertTrue(statusResult.HasWorkingDirectory, "Workspace status should report the prepared working directory.");

                RemoteTunnelRequestResult workspaceTree = await fixture.Service.HandleAsync(
                    RemoteTunnelProtocol.CreateRequest("armada.workspace.tree", new
                    {
                        vesselId = vessel.Id
                    }),
                    CancellationToken.None).ConfigureAwait(false);

                AssertEqual(200, workspaceTree.StatusCode, "workspace tree");
                AssertContains("notes.txt", SerializePayload(workspaceTree));
                AssertContains("docs", SerializePayload(workspaceTree));

                RemoteTunnelRequestResult workspaceFile = await fixture.Service.HandleAsync(
                    RemoteTunnelProtocol.CreateRequest("armada.workspace.file", new
                    {
                        vesselId = vessel.Id,
                        path = "notes.txt"
                    }),
                    CancellationToken.None).ConfigureAwait(false);

                AssertEqual(200, workspaceFile.StatusCode, "workspace file");
                AssertContains("Proxy workspace token", SerializePayload(workspaceFile));

                RemoteTunnelRequestResult workspaceSearch = await fixture.Service.HandleAsync(
                    RemoteTunnelProtocol.CreateRequest("armada.workspace.search", new
                    {
                        vesselId = vessel.Id,
                        query = "workspace token",
                        maxResults = 10
                    }),
                    CancellationToken.None).ConfigureAwait(false);

                AssertEqual(200, workspaceSearch.StatusCode, "workspace search");
                AssertContains("notes.txt", SerializePayload(workspaceSearch));

                RemoteTunnelRequestResult workspaceChanges = await fixture.Service.HandleAsync(
                    RemoteTunnelProtocol.CreateRequest("armada.workspace.changes", new
                    {
                        vesselId = vessel.Id
                    }),
                    CancellationToken.None).ConfigureAwait(false);

                AssertEqual(200, workspaceChanges.StatusCode, "workspace changes");

                RemoteTunnelRequestResult pipelineList = await fixture.Service.HandleAsync(
                    RemoteTunnelProtocol.CreateRequest("armada.pipelines.list", new EnumerationQuery
                    {
                        PageNumber = 1,
                        PageSize = 20
                    }),
                    CancellationToken.None).ConfigureAwait(false);

                AssertEqual(200, pipelineList.StatusCode, "pipeline list");
                AssertContains("Proxy Reference Pipeline", SerializePayload(pipelineList));

                RemoteTunnelRequestResult pipelineDetail = await fixture.Service.HandleAsync(
                    RemoteTunnelProtocol.CreateRequest("armada.pipeline.detail", new
                    {
                        pipelineId = pipeline.Name
                    }),
                    CancellationToken.None).ConfigureAwait(false);

                AssertEqual(200, pipelineDetail.StatusCode, "pipeline detail");
                AssertContains("Proxy Reference Pipeline", SerializePayload(pipelineDetail));

                RemoteTunnelRequestResult personaList = await fixture.Service.HandleAsync(
                    RemoteTunnelProtocol.CreateRequest("armada.personas.list", new EnumerationQuery
                    {
                        PageNumber = 1,
                        PageSize = 20
                    }),
                    CancellationToken.None).ConfigureAwait(false);

                AssertEqual(200, personaList.StatusCode, "persona list");
                AssertContains("Proxy Analyst", SerializePayload(personaList));

                RemoteTunnelRequestResult personaDetail = await fixture.Service.HandleAsync(
                    RemoteTunnelProtocol.CreateRequest("armada.persona.detail", new
                    {
                        personaId = persona.Name
                    }),
                    CancellationToken.None).ConfigureAwait(false);

                AssertEqual(200, personaDetail.StatusCode, "persona detail");
                AssertContains("Proxy Analyst", SerializePayload(personaDetail));

                RemoteTunnelRequestResult promptTemplateList = await fixture.Service.HandleAsync(
                    RemoteTunnelProtocol.CreateRequest("armada.prompt-templates.list", new EnumerationQuery
                    {
                        PageNumber = 1,
                        PageSize = 20
                    }),
                    CancellationToken.None).ConfigureAwait(false);

                AssertEqual(200, promptTemplateList.StatusCode, "prompt-template list");
                AssertContains("persona.worker", SerializePayload(promptTemplateList));

                RemoteTunnelRequestResult promptTemplateDetail = await fixture.Service.HandleAsync(
                    RemoteTunnelProtocol.CreateRequest("armada.prompt-template.detail", new
                    {
                        id = "persona.worker"
                    }),
                    CancellationToken.None).ConfigureAwait(false);

                AssertEqual(200, promptTemplateDetail.StatusCode, "prompt-template detail");
                AssertContains("persona.worker", SerializePayload(promptTemplateDetail));
            }).ConfigureAwait(false);
        }

        private static JsonDocument ParsePayload(RemoteTunnelRequestResult result)
        {
            return JsonDocument.Parse(SerializePayload(result));
        }

        private static T DeserializePayload<T>(RemoteTunnelRequestResult result)
        {
            return JsonSerializer.Deserialize<T>(SerializePayload(result), RemoteTunnelProtocol.JsonOptions)
                ?? throw new InvalidOperationException("Unable to deserialize payload as " + typeof(T).Name + ".");
        }

        private static string SerializePayload(RemoteTunnelRequestResult result)
        {
            return JsonSerializer.Serialize(result.Payload, RemoteTunnelProtocol.JsonOptions);
        }

        private sealed class OperationsFixture : IDisposable
        {
            public SqliteDatabaseDriver Database { get; }
            public ArmadaSettings Settings { get; }
            public StubGitService Git { get; }
            public ObjectiveService Objectives { get; }
            public ObjectiveRefinementCoordinator ObjectiveRefinement { get; }
            public PlanningSessionCoordinator PlanningSessions { get; }
            public WorkflowProfileService WorkflowProfiles { get; }
            public VesselReadinessService VesselReadiness { get; }
            public CheckRunService CheckRuns { get; }
            public DeploymentEnvironmentService Environments { get; }
            public ReleaseService Releases { get; }
            public DeploymentService Deployments { get; }
            public IncidentService Incidents { get; }
            public RunbookService Runbooks { get; }
            public CaptainToolService CaptainTools { get; }
            public WorkspaceService Workspace { get; }
            public PromptTemplateService PromptTemplates { get; }
            public RemoteControlOperationsService Service { get; }

            private readonly string _rootDirectory;
            private readonly LoggingModule _logging;

            public OperationsFixture(SqliteDatabaseDriver database)
            {
                Database = database;
                _logging = CreateLogging();
                _rootDirectory = Path.Combine(Path.GetTempPath(), "armada_remote_operations_" + Guid.NewGuid().ToString("N"));

                Settings = new ArmadaSettings
                {
                    DataDirectory = _rootDirectory,
                    DatabasePath = Path.Combine(_rootDirectory, "armada.db"),
                    LogDirectory = Path.Combine(_rootDirectory, "logs"),
                    DocksDirectory = Path.Combine(_rootDirectory, "docks"),
                    ReposDirectory = Path.Combine(_rootDirectory, "repos")
                };
                Settings.InitializeDirectories();

                Git = new StubGitService();
                Objectives = new ObjectiveService(Database);

                ObjectiveRefinement = new ObjectiveRefinementCoordinator(
                    _logging,
                    Database,
                    Settings,
                    new AgentRuntimeFactory(_logging),
                    (_, _, _, _, _, _, _, _) => Task.CompletedTask);

                DockService docks = new DockService(_logging, Database, Settings, Git);
                AdmiralService admiral = CreateAdmiralService(_logging, Database, Settings, Git);
                PlanningSessions = new PlanningSessionCoordinator(
                    _logging,
                    Database,
                    Settings,
                    docks,
                    admiral,
                    new AgentRuntimeFactory(_logging),
                    (_, _, _, _, _, _, _, _) => Task.CompletedTask);

                WorkflowProfiles = new WorkflowProfileService(Database, _logging);
                VesselReadiness = new VesselReadinessService(Database, WorkflowProfiles, _logging);
                CheckRuns = new CheckRunService(Database, WorkflowProfiles, VesselReadiness, _logging);
                Environments = new DeploymentEnvironmentService(Database, WorkflowProfiles, _logging);
                Releases = new ReleaseService(Database, WorkflowProfiles, _logging);
                Deployments = new DeploymentService(Database, WorkflowProfiles, Environments, CheckRuns, _logging);
                Incidents = new IncidentService(Database);
                Runbooks = new RunbookService(Database, _logging);
                CaptainTools = new CaptainToolService(_logging, Database);
                Workspace = new WorkspaceService();
                PromptTemplates = new PromptTemplateService(Database, _logging);
                Service = new RemoteControlOperationsService(
                    Database,
                    Objectives,
                    ObjectiveRefinement,
                    PlanningSessions,
                    WorkflowProfiles,
                    Environments,
                    CheckRuns,
                    Releases,
                    Deployments,
                    Incidents,
                    Runbooks,
                    CaptainTools,
                    Workspace,
                    PromptTemplates);
            }

            public async Task EnsureDefaultPrincipalAsync()
            {
                TenantMetadata? tenant = await Database.Tenants.ReadAsync(Constants.DefaultTenantId).ConfigureAwait(false);
                if (tenant == null)
                {
                    await Database.Tenants.CreateAsync(new TenantMetadata
                    {
                        Id = Constants.DefaultTenantId,
                        Name = "Default Tenant"
                    }).ConfigureAwait(false);
                }

                UserMaster? user = await Database.Users.ReadByIdAsync(Constants.DefaultUserId).ConfigureAwait(false);
                if (user == null)
                {
                    await Database.Users.CreateAsync(new UserMaster
                    {
                        Id = Constants.DefaultUserId,
                        TenantId = Constants.DefaultTenantId,
                        Email = "proxy@armada.test",
                        PasswordSha256 = UserMaster.ComputePasswordHash("password"),
                        IsTenantAdmin = true
                    }).ConfigureAwait(false);
                }
            }

            public async Task<Vessel> CreateVesselAsync(string name, bool initializeRepository = false)
            {
                Fleet fleet = new Fleet("fleet-" + name)
                {
                    TenantId = Constants.DefaultTenantId,
                    UserId = Constants.DefaultUserId
                };
                fleet = await Database.Fleets.CreateAsync(fleet).ConfigureAwait(false);

                string repoPath = Path.Combine(Settings.ReposDirectory, name + ".git");
                string worktreePath = Path.Combine(Settings.ReposDirectory, name + "-workspace");
                Directory.CreateDirectory(repoPath);
                Directory.CreateDirectory(worktreePath);

                if (initializeRepository)
                {
                    Directory.CreateDirectory(Path.Combine(worktreePath, ".git"));
                }

                Vessel vessel = new Vessel(name, "https://github.com/test/" + name + ".git")
                {
                    TenantId = Constants.DefaultTenantId,
                    UserId = Constants.DefaultUserId,
                    FleetId = fleet.Id,
                    LocalPath = repoPath,
                    WorkingDirectory = worktreePath,
                    DefaultBranch = "main",
                    ProjectContext = "Proxy operations fixture"
                };

                return await Database.Vessels.CreateAsync(vessel).ConfigureAwait(false);
            }

            public async Task<Captain> CreateCaptainAsync(string name, AgentRuntimeEnum runtime)
            {
                Captain captain = new Captain(name, runtime)
                {
                    TenantId = Constants.DefaultTenantId,
                    UserId = Constants.DefaultUserId,
                    State = CaptainStateEnum.Idle
                };
                return await Database.Captains.CreateAsync(captain).ConfigureAwait(false);
            }

            public async Task<Pipeline> CreatePipelineAsync(string name)
            {
                Pipeline pipeline = new Pipeline(name)
                {
                    TenantId = Constants.DefaultTenantId,
                    Description = "Proxy operations pipeline",
                    Stages = new List<PipelineStage>
                    {
                        new PipelineStage(1, "Architect"),
                        new PipelineStage(2, "Worker"),
                        new PipelineStage(3, "Judge")
                    }
                };
                return await Database.Pipelines.CreateAsync(pipeline).ConfigureAwait(false);
            }

            public async Task<Objective> CreateObjectiveAsync(string title, Vessel vessel)
            {
                Objective created = await Objectives.CreateAsync(
                    AuthContext.Authenticated(Constants.DefaultTenantId, Constants.DefaultUserId, false, true, "UnitTest"),
                    new ObjectiveUpsertRequest
                    {
                        Title = title,
                        Description = title + " description",
                        Status = ObjectiveStatusEnum.Draft,
                        BacklogState = ObjectiveBacklogStateEnum.Inbox,
                        VesselIds = new List<string> { vessel.Id },
                        FleetIds = !String.IsNullOrWhiteSpace(vessel.FleetId) ? new List<string> { vessel.FleetId } : new List<string>()
                    }).ConfigureAwait(false);

                return created;
            }

            public async Task<Objective> RequireObjectiveAsync(string objectiveId)
            {
                Objective? objective = await Database.Objectives.ReadAsync(objectiveId).ConfigureAwait(false);
                return objective ?? throw new InvalidOperationException("Objective not found: " + objectiveId);
            }

            public void Dispose()
            {
                try
                {
                    if (Directory.Exists(_rootDirectory))
                        Directory.Delete(_rootDirectory, true);
                }
                catch
                {
                }
            }

            private static LoggingModule CreateLogging()
            {
                LoggingModule logging = new LoggingModule();
                logging.Settings.EnableConsole = false;
                return logging;
            }

            private static AdmiralService CreateAdmiralService(
                LoggingModule logging,
                DatabaseDriver database,
                ArmadaSettings settings,
                StubGitService git)
            {
                IDockService dockService = new DockService(logging, database, settings, git);
                ICaptainService captainService = new CaptainService(logging, database, settings, git, dockService);
                IMissionService missionService = new MissionService(logging, database, settings, dockService, captainService);
                IVoyageService voyageService = new VoyageService(logging, database);
                return new AdmiralService(logging, database, settings, captainService, missionService, voyageService, dockService);
            }
        }
    }
}
