namespace Armada.Server.Mcp.Tools
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Core.Authorization;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;

    /// <summary>
    /// Registers the MCP enumerate tool for paginated, filtered entity browsing.
    /// </summary>
    public static class McpEnumerateTools
    {
        private static readonly JsonSerializerOptions _JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        // The arguments the tool's input schema declares. An argument outside this set is refused by name, so a
        // misspelt filter fails loudly instead of returning an unfiltered listing.
        private static readonly HashSet<string> _AcceptedArguments = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "entityType", "pageNumber", "pageSize", "order", "createdAfter", "createdBefore", "status",
            "fleetId", "vesselId", "captainId", "voyageId", "missionId", "eventType", "signalType",
            "toCaptainId", "unreadOnly", "includeDescription", "includeContext", "includeTestOutput",
            "includePayload", "includeMessage"
        };

        // A mission caller's mission and voyage listings are filtered record by record, so the candidates are
        // read in pages of this size.
        private const int _CandidatePageSize = 1000;

        /// <summary>
        /// Registers the enumerate MCP tool with the server. Every entity type lists only the records the
        /// caller may read under the same rule as that entity's per-id read: a global administrator lists
        /// every record, a tenant administrator its own tenant's records, and any other caller the records it
        /// owns. A mission caller never lists with its owner's administrator rights, and its mission and voyage
        /// listings hold exactly the records armada_mission_status and armada_voyage_status let it read.
        /// </summary>
        /// <param name="register">Delegate to register each tool.</param>
        /// <param name="database">Database driver for entity enumeration.</param>
        /// <param name="mergeQueue">Optional merge queue service for merge queue enumeration.</param>
        public static void Register(RegisterToolDelegate register, DatabaseDriver database, IMergeQueueService? mergeQueue = null)
        {
            register(
                "armada_enumerate",
                "Find and browse entities with paginated, filtered, sorted access to: fleets, vessels, captains, missions, voyages, docks, signals, events, merge_queue, personas, prompt_templates, pipelines, playbooks, objectives, incidents, checks, releases, deployments. Returns paginated results with total counts. Filter by vesselId, fleetId, captainId, voyageId, status, date range, and more.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        entityType = new { type = "string", description = "Entity type to enumerate: fleets, vessels, captains, missions, voyages, docks, signals, events, merge_queue, personas, prompt_templates, pipelines, playbooks, objectives, incidents, checks, releases, deployments, memories" },
                        pageNumber = new { type = "integer", description = "Page number (1-based, default 1)" },
                        pageSize = new { type = "integer", description = "Results per page (default 10, max 1000)" },
                        order = new { type = "string", description = "Sort order: CreatedAscending, CreatedDescending (default)" },
                        createdAfter = new { type = "string", description = "ISO 8601 timestamp — only return entities created after this time" },
                        createdBefore = new { type = "string", description = "ISO 8601 timestamp — only return entities created before this time" },
                        status = new { type = "string", description = "Filter by status (entity-specific: Pending/InProgress/Complete/Failed/Cancelled for missions, Active/Complete/Cancelled for voyages, Idle/Working/Stalled for captains, Queued/Testing/Passed/Failed/Landed/Cancelled for merge queue)" },
                        fleetId = new { type = "string", description = "Filter by fleet ID (vessels)" },
                        vesselId = new { type = "string", description = "Filter by vessel ID (missions, docks)" },
                        captainId = new { type = "string", description = "Filter by captain ID (missions, events, signals)" },
                        voyageId = new { type = "string", description = "Filter by voyage ID (missions, events)" },
                        missionId = new { type = "string", description = "Filter by mission ID (events)" },
                        eventType = new { type = "string", description = "Filter by event type string (events only)" },
                        signalType = new { type = "string", description = "Filter by signal type (signals only)" },
                        toCaptainId = new { type = "string", description = "Filter by recipient captain ID (signals only)" },
                        unreadOnly = new { type = "boolean", description = "Return only unread signals (signals only)" },
                        includeDescription = new { type = "boolean", description = "Include full Description on voyages (default false; returns descriptionLength hint when false). Mission enumeration always returns lightweight mission summaries; use mission-specific tools for details/logs/diffs." },
                        includeContext = new { type = "boolean", description = "Include ProjectContext and StyleGuide on vessels (default false; returns length hints when false)" },
                        includeTestOutput = new { type = "boolean", description = "Include TestOutput on merge queue entries and Output on checks (default false; returns testOutputLength / outputLength hints when false)" },
                        includePayload = new { type = "boolean", description = "Include full Payload on events (default false; returns payloadLength hint when false)" },
                        includeMessage = new { type = "boolean", description = "Include each signal's stored Payload on signals (default false; returns PayloadLength when false)." }
                    },
                    required = new[] { "entityType" }
                },
                async (args) =>
                {
                    List<string> unknown = UnknownArguments(args);
                    if (unknown.Count > 0)
                    {
                        return (object)new
                        {
                            Error = "Unknown argument(s) for armada_enumerate: " + String.Join(", ", unknown)
                                + ". Accepted arguments: " + String.Join(", ", _AcceptedArguments) + "."
                        };
                    }

                    EnumerateArgs request = JsonSerializer.Deserialize<EnumerateArgs>(args!.Value, _JsonOptions)!;
                    string entityType = (request.EntityType ?? "").ToLowerInvariant();
                    EnumerationQuery query = request.ToEnumerationQuery();
                    AuthContext caller = McpCallerContext.Require();
                    AuthContext listCaller = String.IsNullOrEmpty(caller.MissionId)
                        ? caller
                        : McpMissionEvidenceScope.OwnerScopeOf(caller);
                    CallerRecordScope scope = CallerScopedRead.ResolveScope(listCaller);

                    switch (entityType)
                    {
                        case "fleets":
                        case "fleet":
                            EnumerationResult<Fleet> fleets = await CallerScopedRead.EnumerateAsync(scope, query,
                                q => database.Fleets.EnumerateAsync(q),
                                (t, q) => database.Fleets.EnumerateAsync(t, q),
                                (t, u, q) => database.Fleets.EnumerateAsync(t, u, q)).ConfigureAwait(false);
                            return (object)fleets;
                        case "vessels":
                        case "vessel":
                            EnumerationResult<Vessel> vessels = await CallerScopedRead.EnumerateAsync(scope, query,
                                q => database.Vessels.EnumerateAsync(q),
                                (t, q) => database.Vessels.EnumerateAsync(t, q),
                                (t, u, q) => database.Vessels.EnumerateAsync(t, u, q)).ConfigureAwait(false);
                            if (request.IncludeContext != true)
                            {
                                object projectedVessels = new
                                {
                                    vessels.Success,
                                    vessels.PageNumber,
                                    vessels.PageSize,
                                    vessels.TotalPages,
                                    vessels.TotalRecords,
                                    Objects = vessels.Objects.Select(v => new
                                    {
                                        v.Id, v.FleetId, v.Name, v.RepoUrl, v.LocalPath, v.WorkingDirectory,
                                        v.DefaultBranch, v.LandingMode, v.BranchCleanupPolicy,
                                        v.AllowConcurrentMissions, v.Active, v.CreatedUtc, v.LastUpdateUtc,
                                        ProjectContextLength = v.ProjectContext?.Length ?? 0,
                                        StyleGuideLength = v.StyleGuide?.Length ?? 0,
                                        v.EnableModelContext,
                                        ModelContextLength = v.ModelContext?.Length ?? 0
                                    }).ToList(),
                                    vessels.TotalMs
                                };
                                return (object)projectedVessels;
                            }
                            return (object)vessels;
                        case "captains":
                        case "captain":
                            EnumerationResult<Captain> captains = await CallerScopedRead.EnumerateAsync(scope, query,
                                q => database.Captains.EnumerateAsync(q),
                                (t, q) => database.Captains.EnumerateAsync(t, q),
                                (t, u, q) => database.Captains.EnumerateAsync(t, u, q)).ConfigureAwait(false);
                            object projectedCaptains = new
                            {
                                captains.Success,
                                captains.PageNumber,
                                captains.PageSize,
                                captains.TotalPages,
                                captains.TotalRecords,
                                Objects = captains.Objects.Select(c => new
                                {
                                    c.Id, c.TenantId, c.UserId, c.Name, c.Runtime, c.State,
                                    c.CurrentMissionId, c.CurrentDockId, c.ProcessId,
                                    c.RecoveryAttempts, c.LastHeartbeatUtc, c.CreatedUtc, c.LastUpdateUtc,
                                    SystemInstructionsLength = c.SystemInstructions?.Length ?? 0
                                }).ToList(),
                                captains.TotalMs
                            };
                            return (object)projectedCaptains;
                        case "missions":
                        case "mission":
                            EnumerationResult<Mission> missions = String.IsNullOrEmpty(caller.MissionId)
                                ? await CallerScopedRead.EnumerateAsync(scope, query,
                                    q => database.Missions.EnumerateSummariesAsync(q),
                                    (t, q) => database.Missions.EnumerateSummariesAsync(t, q),
                                    (t, u, q) => database.Missions.EnumerateSummariesAsync(t, u, q)).ConfigureAwait(false)
                                : await EnumerateMissionsForMissionCallerAsync(database, caller, query).ConfigureAwait(false);
                            object projectedMissions = new
                            {
                                missions.Success,
                                missions.PageNumber,
                                missions.PageSize,
                                missions.TotalPages,
                                missions.TotalRecords,
                                Objects = missions.Objects.Select(m => new
                                {
                                    m.Id, m.Title, m.Status, m.VesselId, m.VoyageId, m.CaptainId,
                                    m.BranchName, m.DockId, m.ProcessId, m.PrUrl, m.CommitHash,
                                    m.Priority, m.ParentMissionId, m.Persona, m.DependsOnMissionId,
                                    m.FailureReason, m.TotalRuntimeMs,
                                    m.CreatedUtc, m.LastUpdateUtc, m.StartedUtc, m.CompletedUtc,
                                    DescriptionLength = m.Description?.Length ?? 0,
                                    AgentOutputLength = m.AgentOutput?.Length ?? 0
                                }).ToList(),
                                missions.TotalMs
                            };
                            return (object)projectedMissions;
                        case "voyages":
                        case "voyage":
                            EnumerationResult<Voyage> voyages = String.IsNullOrEmpty(caller.MissionId)
                                ? await CallerScopedRead.EnumerateAsync(scope, query,
                                    q => database.Voyages.EnumerateAsync(q),
                                    (t, q) => database.Voyages.EnumerateAsync(t, q),
                                    (t, u, q) => database.Voyages.EnumerateAsync(t, u, q)).ConfigureAwait(false)
                                : await EnumerateVoyagesForMissionCallerAsync(database, caller, query).ConfigureAwait(false);
                            if (request.IncludeDescription != true)
                            {
                                object projectedVoyages = new
                                {
                                    voyages.Success,
                                    voyages.PageNumber,
                                    voyages.PageSize,
                                    voyages.TotalPages,
                                    voyages.TotalRecords,
                                    Objects = voyages.Objects.Select(v => new
                                    {
                                        v.Id, v.Title, v.Status, v.CreatedUtc, v.CompletedUtc, v.LastUpdateUtc,
                                        v.AutoPush, v.AutoCreatePullRequests, v.AutoMergePullRequests, v.LandingMode,
                                        DescriptionLength = v.Description?.Length ?? 0
                                    }).ToList(),
                                    voyages.TotalMs
                                };
                                return (object)projectedVoyages;
                            }
                            return (object)voyages;
                        case "docks":
                        case "dock":
                            EnumerationResult<Dock> docks = await CallerScopedRead.EnumerateAsync(scope, query,
                                q => database.Docks.EnumerateAsync(q),
                                (t, q) => database.Docks.EnumerateAsync(t, q),
                                (t, u, q) => database.Docks.EnumerateAsync(t, u, q)).ConfigureAwait(false);
                            return (object)docks;
                        case "signals":
                        case "signal":
                            EnumerationResult<Signal> signals = await CallerScopedRead.EnumerateAsync(scope, query,
                                q => database.Signals.EnumerateAsync(q),
                                (t, q) => database.Signals.EnumerateAsync(t, q),
                                (t, u, q) => database.Signals.EnumerateAsync(t, u, q)).ConfigureAwait(false);
                            if (request.IncludeMessage != true)
                            {
                                object projectedSignals = new
                                {
                                    signals.Success,
                                    signals.PageNumber,
                                    signals.PageSize,
                                    signals.TotalPages,
                                    signals.TotalRecords,
                                    Objects = signals.Objects.Select(s => new
                                    {
                                        s.Id, s.FromCaptainId, s.ToCaptainId, s.Type, s.Read, s.CreatedUtc,
                                        PayloadLength = s.Payload?.Length ?? 0
                                    }).ToList(),
                                    signals.TotalMs
                                };
                                return (object)projectedSignals;
                            }
                            return (object)signals;
                        case "events":
                        case "event":
                            EnumerationResult<ArmadaEvent> events = await CallerScopedRead.EnumerateAsync(scope, query,
                                q => database.Events.EnumerateAsync(q),
                                (t, q) => database.Events.EnumerateAsync(t, q),
                                (t, u, q) => database.Events.EnumerateAsync(t, u, q)).ConfigureAwait(false);
                            if (request.IncludePayload != true)
                            {
                                object projectedEvents = new
                                {
                                    events.Success,
                                    events.PageNumber,
                                    events.PageSize,
                                    events.TotalPages,
                                    events.TotalRecords,
                                    Objects = events.Objects.Select(e => new
                                    {
                                        e.Id, e.EventType, e.EntityType, e.EntityId,
                                        e.CaptainId, e.MissionId, e.VesselId, e.VoyageId,
                                        e.Message, e.CreatedUtc,
                                        PayloadLength = e.Payload?.Length ?? 0
                                    }).ToList(),
                                    events.TotalMs
                                };
                                return (object)projectedEvents;
                            }
                            return (object)events;
                        case "merge_queue":
                        case "merge-queue":
                        case "mergequeue":
                        case "merge_entries":
                            EnumerationResult<MergeEntry> mqResult = await CallerScopedRead.EnumerateAsync(scope, query,
                                q => database.MergeEntries.EnumerateAsync(q),
                                (t, q) => database.MergeEntries.EnumerateAsync(t, q),
                                (t, u, q) => database.MergeEntries.EnumerateAsync(t, u, q)).ConfigureAwait(false);
                            if (request.IncludeTestOutput != true)
                            {
                                object projectedMerge = new
                                {
                                    mqResult.Success,
                                    mqResult.PageNumber,
                                    mqResult.PageSize,
                                    mqResult.TotalPages,
                                    mqResult.TotalRecords,
                                    Objects = mqResult.Objects.Select(e => new
                                    {
                                        e.Id, e.MissionId, e.VesselId, e.BranchName, e.TargetBranch,
                                        e.Status, e.Priority, e.BatchId, e.TestCommand, e.TestExitCode,
                                        e.CreatedUtc, e.LastUpdateUtc, e.TestStartedUtc, e.CompletedUtc,
                                        TestOutputLength = e.TestOutput?.Length ?? 0
                                    }).ToList(),
                                    mqResult.TotalMs
                                };
                                return (object)projectedMerge;
                            }
                            return (object)mqResult;
                        case "personas":
                        case "persona":
                            EnumerationResult<Persona> personas = OwnedRecordScope.Page(
                                await database.Personas.EnumerateAsync().ConfigureAwait(false), caller, query);
                            return (object)personas;
                        case "prompt_templates":
                        case "prompt_template":
                        case "templates":
                        case "template":
                            EnumerationResult<PromptTemplate> templates = OwnedRecordScope.Page(
                                await database.PromptTemplates.EnumerateAsync().ConfigureAwait(false), caller, query);
                            if (request.IncludeDescription != true)
                            {
                                object projectedTemplates = new
                                {
                                    templates.Success,
                                    templates.PageNumber,
                                    templates.PageSize,
                                    templates.TotalPages,
                                    templates.TotalRecords,
                                    Objects = templates.Objects.Select(t => new
                                    {
                                        t.Id, t.Name, t.Description, t.Category, t.IsBuiltIn, t.Active,
                                        t.CreatedUtc, t.LastUpdateUtc,
                                        ContentLength = t.Content?.Length ?? 0
                                    }).ToList(),
                                    templates.TotalMs
                                };
                                return (object)projectedTemplates;
                            }
                            return (object)templates;
                        case "memories":
                        case "memory":
                            EnumerationResult<Memory> memories = await new MemoryService(database)
                                .EnumerateAsync(caller, query).ConfigureAwait(false);
                            return (object)memories;
                        case "pipelines":
                        case "pipeline":
                            EnumerationResult<Pipeline> pipelines = OwnedRecordScope.Page(
                                await database.Pipelines.EnumerateAsync().ConfigureAwait(false), caller, query);
                            return (object)pipelines;
                        case "playbooks":
                        case "playbook":
                            EnumerationResult<Playbook> playbooks = await PlaybookService.EnumerateForCallerAsync(database, listCaller, query).ConfigureAwait(false);
                            if (request.IncludeDescription != true)
                            {
                                object projectedPlaybooks = new
                                {
                                    playbooks.Success,
                                    playbooks.PageNumber,
                                    playbooks.PageSize,
                                    playbooks.TotalPages,
                                    playbooks.TotalRecords,
                                    Objects = playbooks.Objects.Select(p => new
                                    {
                                        p.Id, p.TenantId, p.UserId, p.FileName, p.Description, p.Active,
                                        p.CreatedUtc, p.LastUpdateUtc,
                                        ContentLength = p.Content?.Length ?? 0
                                    }).ToList(),
                                    playbooks.TotalMs
                                };
                                return (object)projectedPlaybooks;
                            }
                            return (object)playbooks;
                        case "objectives":
                        case "objective":
                        case "backlog":
                        case "backlog_items":
                            ObjectiveService objectiveService = new ObjectiveService(database);
                            AuthContext objectiveCaller = caller;
                            // A mission caller lists exactly the objectives get_objective lets it read.
                            ISet<string>? readableObjectiveIds = String.IsNullOrEmpty(objectiveCaller.MissionId)
                                ? null
                                : await McpMissionEvidenceScope.ReadLinkedObjectiveIdsAsync(database, objectiveCaller).ConfigureAwait(false);
                            return (object)await objectiveService.EnumerateAsync(
                                objectiveCaller,
                                request.ToObjectiveQuery(),
                                readableObjectiveIds).ConfigureAwait(false);
                        case "incidents":
                        case "incident":
                            IncidentService incidentService = new IncidentService(database);
                            return (object)await incidentService.EnumerateAsync(
                                listCaller,
                                request.ToIncidentQuery()).ConfigureAwait(false);
                        case "checks":
                        case "check":
                        case "check_runs":
                        case "check_run":
                            CheckRunQuery checkRunQuery = request.ToCheckRunQuery();
                            checkRunQuery.TenantId = scope.TenantId;
                            checkRunQuery.UserId = scope.UserId;
                            // A Check's Output is a whole build or test log. The browse path does not select it
                            // unless the caller opts in, so a page of Checks never loads their logs.
                            checkRunQuery.IncludeOutput = request.IncludeTestOutput == true;
                            EnumerationResult<CheckRun> checkRuns = scope.Kind == CallerRecordScopeKindEnum.None
                                ? EnumerationResult<CheckRun>.Create(query, new List<CheckRun>(), 0)
                                : await database.CheckRuns.EnumerateAsync(checkRunQuery).ConfigureAwait(false);
                            if (request.IncludeTestOutput != true)
                            {
                                // The browse path withholds the log and says how long it is, as computed by the
                                // database; get_check_run returns it whole. Summary, the structured test and
                                // coverage summaries and the artifact list are small and stay.
                                object projectedChecks = new
                                {
                                    checkRuns.Success,
                                    checkRuns.PageNumber,
                                    checkRuns.PageSize,
                                    checkRuns.TotalPages,
                                    checkRuns.TotalRecords,
                                    Objects = checkRuns.Objects.Select(c => new
                                    {
                                        c.Id, c.TenantId, c.UserId, c.WorkflowProfileId, c.VesselId, c.MissionId, c.VoyageId, c.DeploymentId,
                                        c.Label, c.Type, c.Source, c.Status, c.ProviderName, c.ExternalId, c.ExternalUrl, c.EnvironmentName,
                                        c.Command, c.WorkingDirectory, c.BranchName, c.CommitHash, c.ExitCode, c.Summary, c.TestSummary,
                                        c.CoverageSummary, c.Artifacts, c.DurationMs, c.QueueDurationMs, c.StartedUtc, c.CompletedUtc, c.CreatedUtc, c.LastUpdateUtc,
                                        OutputLength = c.OutputLength ?? 0
                                    }).ToList(),
                                    checkRuns.TotalMs
                                };
                                return (object)projectedChecks;
                            }
                            return (object)checkRuns;
                        case "releases":
                        case "release":
                            if (scope.Kind == CallerRecordScopeKindEnum.None) return (object)EnumerationResult<Release>.Create(query, new List<Release>(), 0);
                            ReleaseQuery releaseQuery = request.ToReleaseQuery();
                            releaseQuery.TenantId = scope.TenantId;
                            releaseQuery.UserId = scope.UserId;
                            return (object)await database.Releases.EnumerateAsync(releaseQuery).ConfigureAwait(false);
                        case "deployments":
                        case "deployment":
                            if (scope.Kind == CallerRecordScopeKindEnum.None) return (object)EnumerationResult<Deployment>.Create(query, new List<Deployment>(), 0);
                            DeploymentQuery deploymentQuery = request.ToDeploymentQuery();
                            deploymentQuery.TenantId = scope.TenantId;
                            deploymentQuery.UserId = scope.UserId;
                            return (object)await database.Deployments.EnumerateAsync(deploymentQuery).ConfigureAwait(false);
                        default:
                            return (object)new { Error = "Unknown entity type: " + entityType + ". Valid types: fleets, vessels, captains, missions, voyages, docks, signals, events, merge_queue, personas, prompt_templates, pipelines, playbooks, objectives, incidents, checks, releases, deployments" };
                    }
                });
        }

        private sealed class EnumerateArgs
        {
            public string? EntityType { get; set; }
            public int? PageNumber { get; set; }
            public int? PageSize { get; set; }
            public string? Order { get; set; }
            public DateTime? CreatedAfter { get; set; }
            public DateTime? CreatedBefore { get; set; }
            public string? Status { get; set; }
            public string? FleetId { get; set; }
            public string? VesselId { get; set; }
            public string? CaptainId { get; set; }
            public string? VoyageId { get; set; }
            public string? MissionId { get; set; }
            public string? EventType { get; set; }
            public string? SignalType { get; set; }
            public string? ToCaptainId { get; set; }
            public bool? UnreadOnly { get; set; }
            public bool? IncludeDescription { get; set; }
            public bool? IncludeContext { get; set; }
            public bool? IncludeTestOutput { get; set; }
            public bool? IncludePayload { get; set; }
            public bool? IncludeMessage { get; set; }

            public EnumerationQuery ToEnumerationQuery()
            {
                EnumerationQuery query = new EnumerationQuery
                {
                    PageNumber = PageNumber ?? 1,
                    PageSize = PageSize ?? 10,
                    Order = ParseEnum(Order, EnumerationOrderEnum.CreatedDescending),
                    CreatedAfter = CreatedAfter,
                    CreatedBefore = CreatedBefore,
                    Status = Status,
                    FleetId = FleetId,
                    VesselId = VesselId,
                    CaptainId = CaptainId,
                    VoyageId = VoyageId,
                    MissionId = MissionId,
                    EventType = EventType,
                    SignalType = SignalType,
                    ToCaptainId = ToCaptainId,
                    UnreadOnly = UnreadOnly
                };
                return query;
            }

            public ObjectiveQuery ToObjectiveQuery()
            {
                return new ObjectiveQuery
                {
                    VesselId = VesselId,
                    FleetId = FleetId,
                    VoyageId = VoyageId,
                    MissionId = MissionId,
                    Status = ParseNullableEnum<ObjectiveStatusEnum>(Status),
                    PageNumber = PageNumber ?? 1,
                    PageSize = PageSize ?? 25,
                    FromUtc = CreatedAfter,
                    ToUtc = CreatedBefore
                };
            }

            public IncidentQuery ToIncidentQuery()
            {
                return new IncidentQuery
                {
                    VesselId = VesselId,
                    MissionId = MissionId,
                    VoyageId = VoyageId,
                    Status = ParseNullableEnum<IncidentStatusEnum>(Status),
                    PageNumber = PageNumber ?? 1,
                    PageSize = PageSize ?? 25,
                    FromUtc = CreatedAfter,
                    ToUtc = CreatedBefore
                };
            }

            public CheckRunQuery ToCheckRunQuery()
            {
                return new CheckRunQuery
                {
                    VesselId = VesselId,
                    MissionId = MissionId,
                    VoyageId = VoyageId,
                    Status = ParseNullableEnum<CheckRunStatusEnum>(Status),
                    PageNumber = PageNumber ?? 1,
                    PageSize = PageSize ?? 25,
                    FromUtc = CreatedAfter,
                    ToUtc = CreatedBefore
                };
            }

            public ReleaseQuery ToReleaseQuery()
            {
                return new ReleaseQuery
                {
                    VesselId = VesselId,
                    MissionId = MissionId,
                    VoyageId = VoyageId,
                    Status = ParseNullableEnum<ReleaseStatusEnum>(Status),
                    PageNumber = PageNumber ?? 1,
                    PageSize = PageSize ?? 25,
                    FromUtc = CreatedAfter,
                    ToUtc = CreatedBefore
                };
            }

            public DeploymentQuery ToDeploymentQuery()
            {
                return new DeploymentQuery
                {
                    VesselId = VesselId,
                    MissionId = MissionId,
                    VoyageId = VoyageId,
                    Status = ParseNullableEnum<DeploymentStatusEnum>(Status),
                    PageNumber = PageNumber ?? 1,
                    PageSize = PageSize ?? 25,
                    FromUtc = CreatedAfter,
                    ToUtc = CreatedBefore
                };
            }
        }

        private static List<string> UnknownArguments(JsonElement? args)
        {
            List<string> unknown = new List<string>();
            if (!args.HasValue || args.Value.ValueKind != JsonValueKind.Object) return unknown;
            foreach (JsonProperty argument in args.Value.EnumerateObject())
            {
                if (!_AcceptedArguments.Contains(argument.Name)) unknown.Add(argument.Name);
            }
            return unknown;
        }

        private static async Task<EnumerationResult<Mission>> EnumerateMissionsForMissionCallerAsync(
            DatabaseDriver database, AuthContext caller, EnumerationQuery query)
        {
            McpMissionEvidenceScope.MissionReadScope? scope = await McpMissionEvidenceScope.OpenAsync(database, caller).ConfigureAwait(false);
            if (scope == null) return EnumerationResult<Mission>.Create(query, new List<Mission>(), 0);

            // Voyage links decide many mission reads; read every voyage once instead of once per mission.
            List<Voyage> voyages = await ReadAllPagesAsync(new EnumerationQuery(), q => database.Voyages.EnumerateAsync(q)).ConfigureAwait(false);
            await scope.RememberVoyagesAsync(voyages).ConfigureAwait(false);

            List<Mission> candidates = await ReadAllPagesAsync(query, q => database.Missions.EnumerateSummariesAsync(q)).ConfigureAwait(false);
            List<Mission> readable = new List<Mission>();
            foreach (Mission mission in candidates)
            {
                if (await scope.CanReadMissionAsync(mission).ConfigureAwait(false)) readable.Add(mission);
            }
            return Page(readable, query);
        }

        private static async Task<EnumerationResult<Voyage>> EnumerateVoyagesForMissionCallerAsync(
            DatabaseDriver database, AuthContext caller, EnumerationQuery query)
        {
            McpMissionEvidenceScope.MissionReadScope? scope = await McpMissionEvidenceScope.OpenAsync(database, caller).ConfigureAwait(false);
            if (scope == null) return EnumerationResult<Voyage>.Create(query, new List<Voyage>(), 0);

            List<Voyage> candidates = await ReadAllPagesAsync(query, q => database.Voyages.EnumerateAsync(q)).ConfigureAwait(false);
            List<Voyage> readable = new List<Voyage>();
            foreach (Voyage voyage in candidates)
            {
                if (await scope.CanReadVoyageAsync(voyage).ConfigureAwait(false)) readable.Add(voyage);
            }
            return Page(readable, query);
        }

        private static async Task<List<T>> ReadAllPagesAsync<T>(
            EnumerationQuery filters, Func<EnumerationQuery, Task<EnumerationResult<T>>> readPage)
        {
            // Read every record that matches the caller's filters, in the caller's order, one large page at a time.
            int originalPageNumber = filters.PageNumber;
            int originalPageSize = filters.PageSize;
            List<T> all = new List<T>();
            try
            {
                filters.PageSize = _CandidatePageSize;
                for (int pageNumber = 1; ; pageNumber++)
                {
                    filters.PageNumber = pageNumber;
                    EnumerationResult<T> page = await readPage(filters).ConfigureAwait(false);
                    all.AddRange(page.Objects);
                    if (page.Objects.Count < _CandidatePageSize || pageNumber >= page.TotalPages) break;
                }
            }
            finally
            {
                filters.PageNumber = originalPageNumber;
                filters.PageSize = originalPageSize;
            }
            return all;
        }

        private static EnumerationResult<T> Page<T>(List<T> readable, EnumerationQuery query)
        {
            List<T> page = readable.Skip((query.PageNumber - 1) * query.PageSize).Take(query.PageSize).ToList();
            return EnumerationResult<T>.Create(query, page, readable.Count);
        }

        private static TEnum ParseEnum<TEnum>(string? value, TEnum fallback) where TEnum : struct
        {
            return Enum.TryParse(value, true, out TEnum parsed) ? parsed : fallback;
        }

        private static TEnum? ParseNullableEnum<TEnum>(string? value) where TEnum : struct
        {
            return Enum.TryParse(value, true, out TEnum parsed) ? parsed : null;
        }
    }
}
