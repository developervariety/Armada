namespace Armada.Server.Routes
{
    using System.Diagnostics;
    using System.Text.Json;
    using WatsonWebserver;
    using WatsonWebserver.Core;
    using WatsonWebserver.Core.OpenApi;
    using Armada.Server;
    using Armada.Core;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Server.WebSocket;
    using SyslogLogging;

    /// <summary>
    /// REST API routes for voyage management.
    /// </summary>
    public class VoyageRoutes
    {
        private readonly DatabaseDriver _database;
        private readonly IAdmiralService _admiral;
        private readonly Func<string, string, string?, string?, string?, string?, string?, string?, Task> _emitEvent;
        private readonly ArmadaWebSocketHub? _webSocketHub;
        private readonly LoggingModule _logging;
        private readonly ObjectiveService _objectives;
        private readonly ObjectiveDispatchPreviewService? _objectiveDispatchPreview;
        private readonly TypedDispatchStalenessAdapter? _dispatchStalenessAdapter;
        private readonly ICodeIndexService? _codeIndexService;
        private readonly ArmadaSettings? _settings;
        private readonly JsonSerializerOptions _jsonOptions;
        private readonly MissionOperations _operations;

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="admiral">Admiral coordination service.</param>
        /// <param name="emitEvent">Event broadcast callback.</param>
        /// <param name="webSocketHub">WebSocket hub for real-time notifications.</param>
        /// <param name="logging">Logging module.</param>
        /// <param name="objectives">Objective linkage service.</param>
        /// <param name="codeIndexService">Optional code-index service.</param>
        /// <param name="settings">Optional Armada settings.</param>
        /// <param name="jsonOptions">JSON serializer options.</param>
        /// <param name="objectiveDispatchPreview">Optional shared objective dispatch preflight.</param>
        /// <param name="operations">Shared mission and voyage operations. When null, one is built that writes events
        /// through <paramref name="emitEvent"/> and removes no docks, so a mission with a dock is not purged.</param>
        public VoyageRoutes(
            DatabaseDriver database,
            IAdmiralService admiral,
            Func<string, string, string?, string?, string?, string?, string?, string?, Task> emitEvent,
            ArmadaWebSocketHub? webSocketHub,
            LoggingModule logging,
            ObjectiveService objectives,
            ICodeIndexService? codeIndexService,
            ArmadaSettings? settings,
            JsonSerializerOptions jsonOptions,
            ObjectiveDispatchPreviewService? objectiveDispatchPreview = null,
            TypedDispatchStalenessAdapter? dispatchStalenessAdapter = null,
            MissionOperations? operations = null)
        {
            _database = database;
            _admiral = admiral;
            _emitEvent = emitEvent;
            _webSocketHub = webSocketHub;
            _logging = logging;
            _objectives = objectives;
            _codeIndexService = codeIndexService;
            _settings = settings;
            _jsonOptions = jsonOptions;
            _objectiveDispatchPreview = objectiveDispatchPreview;
            _dispatchStalenessAdapter = dispatchStalenessAdapter;
            _operations = operations ?? new MissionOperations(
                database,
                settings ?? new ArmadaSettings(),
                null,
                (captainId, token) => admiral.RecallCaptainAsync(captainId, token),
                new OperationNotifier(emitEvent, mission => webSocketHub?.BroadcastMissionChange(mission), voyage => webSocketHub?.BroadcastVoyageChange(voyage)),
                logging);
        }

        /// <summary>
        /// Determine whether a voyage request should use the existing bare-voyage creation path.
        /// </summary>
        /// <param name="request">Voyage request.</param>
        /// <returns>True when no dispatch should run.</returns>
        public static bool ShouldCreateBareVoyage(VoyageRequest request)
        {
            if (request == null) return true;
            return String.IsNullOrEmpty(request.VesselId)
                || request.Missions == null
                || request.Missions.Count == 0;
        }

        /// <summary>
        /// Convert a REST voyage request into the normalized shared dispatch request.
        /// </summary>
        /// <param name="request">REST request.</param>
        /// <returns>Shared dispatch request.</returns>
        public static SharedVoyageDispatchRequest CreateDispatchRequest(VoyageRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

            List<MissionDescription> missions = new List<MissionDescription>();
            if (request.Missions != null)
            {
                foreach (MissionRequest mission in request.Missions)
                {
                    missions.Add(new MissionDescription
                    {
                        Title = mission.Title,
                        Description = mission.Description,
                        PrestagedFiles = mission.PrestagedFiles,
                        CodeContextMode = mission.CodeContextMode,
                        CodeContextQuery = mission.CodeContextQuery,
                        PreferredModel = mission.PreferredModel,
                        DependsOnMissionId = mission.DependsOnMissionId,
                        Alias = mission.Alias,
                        DependsOnMissionAlias = mission.DependsOnMissionAlias,
                        SelectedPlaybooks = mission.SelectedPlaybooks
                    });
                }
            }

            return new SharedVoyageDispatchRequest
            {
                Title = request.Title,
                Description = request.Description,
                VesselId = request.VesselId,
                Missions = missions,
                CodeContextMode = request.CodeContextMode,
                CodeContextTokenBudget = request.CodeContextTokenBudget,
                CodeContextMaxResults = request.CodeContextMaxResults,
                PipelineId = request.PipelineId,
                Pipeline = request.Pipeline,
                ObjectiveId = request.ObjectiveId,
                ForcePreflight = request.ForcePreflight,
                SelectedPlaybooks = request.SelectedPlaybooks ?? new List<SelectedPlaybook>(),
                CaptainAssignments = request.CaptainAssignments,
                SkipStages = request.SkipStages,
                SkipStagesReason = request.SkipStagesReason
            };
        }

        /// <summary>
        /// Register routes with the application.
        /// </summary>
        /// <param name="app">Webserver.</param>
        /// <param name="authenticate">Authentication middleware.</param>
        /// <param name="authz">Authorization service.</param>
        public void Register(
            Webserver app,
            Func<WatsonWebserver.Core.HttpContextBase, Task<AuthContext>> authenticate,
            IAuthorizationService authz)
        {
            string _Header = "[ArmadaServer] ";

            // Voyages
            app.Get("/api/v1/voyages", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(ctx, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery))
                {
                    return RouteAuthRefusal.Refuse(req, ctx);
                }
                EnumerationQuery query = new EnumerationQuery();
                query.ApplyQuerystringOverrides(key => QueryValueReader.Read(req, key));
                Stopwatch sw = Stopwatch.StartNew();
                EnumerationResult<Voyage> result = ctx.IsAdmin
                    ? await _database.Voyages.EnumerateAsync(query).ConfigureAwait(false)
                    : ctx.IsTenantAdmin
                        ? await _database.Voyages.EnumerateAsync(ctx.TenantId!, query).ConfigureAwait(false)
                        : await _database.Voyages.EnumerateAsync(ctx.TenantId!, ctx.UserId!, query).ConfigureAwait(false);
                result.TotalMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2);
                return result;
            },
            api => api
                .WithTag("Voyages")
                .WithSummary("List all voyages")
                .WithDescription("Returns all voyages, optionally filtered by status (Active, Complete, Cancelled).")
                .WithParameter(OpenApiParameterMetadata.Query("status", "Filter by voyage status", false))
                .WithResponse(200, OpenApiJson.For<EnumerationResult<Voyage>>("Paginated voyage list"))
                .WithSecurity("ApiKey"));

            app.Post<EnumerationQuery>("/api/v1/voyages/enumerate", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(ctx, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery))
                {
                    return RouteAuthRefusal.Refuse(req, ctx);
                }
                EnumerationQuery query = JsonSerializer.Deserialize<EnumerationQuery>(req.Http.Request.DataAsString, _jsonOptions) ?? new EnumerationQuery();
                query.ApplyQuerystringOverrides(key => QueryValueReader.Read(req, key));
                Stopwatch sw = Stopwatch.StartNew();
                EnumerationResult<Voyage> result = ctx.IsAdmin
                    ? await _database.Voyages.EnumerateAsync(query).ConfigureAwait(false)
                    : ctx.IsTenantAdmin
                        ? await _database.Voyages.EnumerateAsync(ctx.TenantId!, query).ConfigureAwait(false)
                        : await _database.Voyages.EnumerateAsync(ctx.TenantId!, ctx.UserId!, query).ConfigureAwait(false);
                result.TotalMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2);
                return result;
            },
            api => api
                .WithTag("Voyages")
                .WithSummary("Enumerate voyages")
                .WithDescription("Paginated enumeration of voyages with optional filtering and sorting.")
                .WithRequestBody(OpenApiJson.BodyFor<EnumerationQuery>("Enumeration query", false))
                .WithSecurity("ApiKey"));

            app.Post<VoyageRequest>("/api/v1/voyages", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(ctx, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery))
                {
                    return RouteAuthRefusal.Refuse(req, ctx);
                }
                VoyageRequest voyageReq = JsonSerializer.Deserialize<VoyageRequest>(req.Http.Request.DataAsString, _jsonOptions)
                    ?? throw new InvalidOperationException("Request body could not be deserialized as VoyageRequest.");
                Objective? linkedObjective = null;
                if (!String.IsNullOrWhiteSpace(voyageReq.ObjectiveId))
                {
                    linkedObjective = await _objectives.ReadAsync(ctx, voyageReq.ObjectiveId).ConfigureAwait(false);
                    if (linkedObjective == null)
                    {
                        req.Http.Response.StatusCode = 404;
                        return new ApiErrorResponse { Error = ApiResultEnum.NotFound, Message = "Objective not found" };
                    }
                }
                bool isBareVoyage = ShouldCreateBareVoyage(voyageReq);
                Voyage voyage;
                if (isBareVoyage)
                {
                    ObjectiveDispatchAdmission? admission = null;
                    if (linkedObjective != null && _objectiveDispatchPreview != null)
                    {
                        ObjectiveDispatchPreview preview = await _objectiveDispatchPreview.PreviewAsync(
                            ctx,
                            linkedObjective,
                            requestedVesselId: voyageReq.VesselId,
                            requestedPipelineId: voyageReq.PipelineId ?? voyageReq.Pipeline,
                            captainAssignments: voyageReq.CaptainAssignments).ConfigureAwait(false);
                        PreflightGateOutcomeEnum outcome = ObjectivePreflightGate.Classify(preview, voyageReq.ForcePreflight);
                        if (outcome == PreflightGateOutcomeEnum.BlockedByPreflight)
                        {
                            req.Http.Response.StatusCode = 400;
                            return new
                            {
                                Error = ObjectivePreflightGate.RefusalMessage,
                                Code = ObjectivePreflightGate.IssueCode,
                                ObjectiveId = linkedObjective.Id,
                                IncompleteQuestions = preview.Preflight.IncompleteQuestions,
                                ModelFlaggedQuestions = ObjectivePreflightGate.ModelFlaggedQuestions(preview),
                                Preview = preview
                            };
                        }
                        if (outcome == PreflightGateOutcomeEnum.BlockedByOther)
                        {
                            req.Http.Response.StatusCode = 400;
                            return new
                            {
                                Error = "Objective dispatch preview found blocking issues.",
                                Code = "objective_dispatch_not_ready",
                                ObjectiveId = linkedObjective.Id,
                                Preview = preview
                            };
                        }
                        if (outcome == PreflightGateOutcomeEnum.OverriddenPreflight)
                        {
                            try
                            {
                                await _database.Events.CreateAsync(
                                    ObjectivePreflightGate.BuildOverrideEvent(linkedObjective, ObjectivePreflightGate.OperatorName(ctx),
                                        ObjectivePreflightGate.ModelFlaggedQuestions(preview))).ConfigureAwait(false);
                            }
                            catch (Exception ex)
                            {
                                _logging.Warn(_Header + "could not record preflight override for objective " + linkedObjective.Id + ": " + ex.Message);
                            }
                        }
                    }

                    if (linkedObjective != null)
                    {
                        try
                        {
                            admission = await _objectives.AcquireDispatchAdmissionAsync(
                                ctx,
                                new[] { linkedObjective.Id },
                                new ObjectiveDispatchAttemptDescriptor { Title = voyageReq.Title, VesselId = voyageReq.VesselId }).ConfigureAwait(false);
                        }
                        catch (ObjectiveAlreadyDispatchedException alreadyDispatched)
                        {
                            req.Http.Response.StatusCode = 409;
                            return new
                            {
                                Error = "Objective already dispatched.",
                                Code = "objective_already_dispatched",
                                ObjectiveId = linkedObjective.Id,
                                VoyageId = alreadyDispatched.WinningVoyageId
                            };
                        }
                        catch (ObjectiveDispatchBusyException busy)
                        {
                            req.Http.Response.StatusCode = 409;
                            return new
                            {
                                Error = "Objective dispatch admission is busy.",
                                Code = "objective_dispatch_busy",
                                ObjectiveId = linkedObjective.Id,
                                Retryable = true,
                                RetryAfterSeconds = (int)Math.Ceiling(busy.RetryAfter.TotalSeconds)
                            };
                        }
                    }

                    Voyage? bareVoyage = null;
                    try
                    {
                        // Bare voyage creation (missions added separately) through the shared create WebSocket uses:
                        // the playbook selections are resolved before anything is stored.
                        BareVoyageResult bare = await _operations.CreateBareVoyageAsync(
                            voyageReq.Title,
                            voyageReq.Description,
                            voyageReq.SelectedPlaybooks,
                            ctx.TenantId,
                            ctx.UserId).ConfigureAwait(false);
                        if (!bare.Succeeded)
                        {
                            req.Http.Response.StatusCode = 400;
                            return new ApiErrorResponse { Error = ApiResultEnum.BadRequest, Message = bare.Message };
                        }
                        bareVoyage = bare.Voyage!;

                        if (linkedObjective != null)
                        {
                            if (admission != null)
                                await admission.RecordVoyageCreatedAsync(bareVoyage).ConfigureAwait(false);
                            admission?.ThrowIfOwnershipLost();
                            await _objectives.LinkVoyageAsync(ctx, linkedObjective.Id, bareVoyage.Id, default, false, admission).ConfigureAwait(false);
                            admission?.MarkLinked();
                        }
                        // Announced after the objective link, so a create that is rolled back never announces a voyage.
                        await VoyageDispatchedEvent.EmitAsync(_database, _logging, bareVoyage, null, 0).ConfigureAwait(false);
                        voyage = bareVoyage;
                    }
                    catch
                    {
                        if (bareVoyage != null)
                        {
                            await VoyageCancellation.CancelVoyageAsync(
                                _database,
                                bareVoyage,
                                "Voyage cancelled: objective " + linkedObjective?.Id + " dispatch did not complete.",
                                CancellationToken.None,
                                _admiral.RecallCaptainAsync).ConfigureAwait(false);
                        }
                        throw;
                    }
                    finally
                    {
                        if (admission != null)
                            await admission.DisposeAsync().ConfigureAwait(false);
                    }
                }
                else
                {
                    SharedVoyageDispatchRequest dispatchRequest = CreateDispatchRequest(voyageReq);
                    dispatchRequest.ObjectiveAuthContext = ctx;
                    dispatchRequest.Settings = _settings;
                    VoyageDispatchService dispatchService = new VoyageDispatchService(
                        _database,
                        _admiral,
                        _logging,
                        _codeIndexService,
                        _objectives,
                        _settings,
                        _objectiveDispatchPreview,
                        _dispatchStalenessAdapter);
                    VoyageDispatchResult dispatchResult = await dispatchService.DispatchAsync(dispatchRequest).ConfigureAwait(false);
                    if (!dispatchResult.Succeeded)
                    {
                        req.Http.Response.StatusCode = dispatchResult.StatusCode;
                        return dispatchResult.Value;
                    }

                    voyage = dispatchResult.Voyage
                        ?? throw new InvalidOperationException("Dispatch succeeded without a voyage response.");
                }

                req.Http.Response.StatusCode = 201;
                return voyage;
            },
            api => api
                .WithTag("Voyages")
                .WithSummary("Create a voyage")
                .WithDescription("Creates a new voyage with optional missions. Missions are automatically dispatched to the target vessel.")
                .WithRequestBody(OpenApiJson.BodyFor<VoyageRequest>("Voyage with missions", true))
                .WithResponse(201, OpenApiJson.For<Voyage>("Created voyage"))
                .WithSecurity("ApiKey"));

            app.Get("/api/v1/voyages/{id}/mission-summary", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(ctx, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery))
                {
                    return RouteAuthRefusal.Refuse(req, ctx);
                }
                int pageNumber = 1;
                int pageSize = 100;
                string? number = QueryValueReader.Read(req, "pageNumber");
                string? size = QueryValueReader.Read(req, "pageSize");
                if ((req.Query.Contains("pageNumber") && !Int32.TryParse(number, out pageNumber))
                    || (req.Query.Contains("pageSize") && !Int32.TryParse(size, out pageSize))
                    || pageNumber < 1 || pageSize < 1 || pageSize > 100)
                {
                    req.Http.Response.StatusCode = 400;
                    return new ApiErrorResponse { Error = ApiResultEnum.BadRequest, Message = "pageNumber must be positive and pageSize must be between 1 and 100." };
                }
                string id = req.Parameters["id"];
                Voyage? voyage = ctx.IsAdmin
                    ? await _database.Voyages.ReadAsync(id).ConfigureAwait(false)
                    : ctx.IsTenantAdmin
                        ? await _database.Voyages.ReadAsync(ctx.TenantId!, id).ConfigureAwait(false)
                        : await _database.Voyages.ReadAsync(ctx.TenantId!, ctx.UserId!, id).ConfigureAwait(false);
                if (voyage == null)
                {
                    req.Http.Response.StatusCode = 404;
                    return new ApiErrorResponse { Error = ApiResultEnum.NotFound, Message = "Voyage not found" };
                }
                return (object)await _database.Missions.ReadVoyageMissionSummaryAsync(id, pageNumber, pageSize,
                    ctx.IsAdmin ? null : ctx.TenantId, ctx.IsAdmin || ctx.IsTenantAdmin ? null : ctx.UserId).ConfigureAwait(false);
            },
            api => api
                .WithTag("Voyages")
                .WithSummary("Read scoped voyage mission counts and vessel associations")
                .WithDescription("Counts all visible missions by status and pages distinct vessel IDs without mission payloads. Scope follows authenticated voyage and mission visibility.")
                .WithParameter(OpenApiParameterMetadata.Path("id", "Voyage ID"))
                .WithParameter(OpenApiParameterMetadata.Query("pageNumber", "Vessel page, starting at 1", false))
                .WithParameter(OpenApiParameterMetadata.Query("pageSize", "Vessels per page, 1 to 100; default 100", false))
                .WithResponse(200, OpenApiJson.For<VoyageMissionSummary>("Scoped counts and vessel page"))
                .WithResponse(404, OpenApiResponseMetadata.NotFound())
                .WithSecurity("ApiKey"));

            app.Get("/api/v1/voyages/{id}", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(ctx, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery))
                {
                    return RouteAuthRefusal.Refuse(req, ctx);
                }
                string id = req.Parameters["id"];
                Voyage? voyage = ctx.IsAdmin
                    ? await _database.Voyages.ReadAsync(id).ConfigureAwait(false)
                    : ctx.IsTenantAdmin
                        ? await _database.Voyages.ReadAsync(ctx.TenantId!, id).ConfigureAwait(false)
                        : await _database.Voyages.ReadAsync(ctx.TenantId!, ctx.UserId!, id).ConfigureAwait(false);
                if (voyage == null) { req.Http.Response.StatusCode = 404; return new ApiErrorResponse { Error = ApiResultEnum.NotFound, Message = "Voyage not found" }; }
                voyage.SelectedPlaybooks = await _database.Playbooks.GetVoyageSelectionsAsync(id).ConfigureAwait(false);
                List<Mission> missions = ctx.IsAdmin
                    ? await _database.Missions.EnumerateByVoyageAsync(id).ConfigureAwait(false)
                    : await _database.Missions.EnumerateByVoyageAsync(ctx.TenantId!, id).ConfigureAwait(false);
                // An ordinary user sees only its own missions, as in the mission list.
                if (!ctx.IsAdmin && !ctx.IsTenantAdmin) missions = missions.Where(mission => mission.UserId == ctx.UserId).ToList();
                foreach (Mission mission in missions)
                {
                    mission.PlaybookSnapshots = await _database.Playbooks.GetMissionSnapshotsAsync(mission.Id).ConfigureAwait(false);
                }
                return (object)new { Voyage = voyage, Missions = missions };
            },
            api => api
                .WithTag("Voyages")
                .WithSummary("Get a voyage")
                .WithDescription("Returns a voyage and all its associated missions.")
                .WithParameter(OpenApiParameterMetadata.Path("id", "Voyage ID (vyg_ prefix)"))
                .WithResponse(404, OpenApiResponseMetadata.NotFound())
                .WithSecurity("ApiKey"));

            app.Delete("/api/v1/voyages/{id}", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(ctx, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery))
                {
                    return RouteAuthRefusal.Refuse(req, ctx);
                }
                string id = req.Parameters["id"];
                Voyage? voyage = ctx.IsAdmin
                    ? await _database.Voyages.ReadAsync(id).ConfigureAwait(false)
                    : ctx.IsTenantAdmin
                        ? await _database.Voyages.ReadAsync(ctx.TenantId!, id).ConfigureAwait(false)
                        : await _database.Voyages.ReadAsync(ctx.TenantId!, ctx.UserId!, id).ConfigureAwait(false);
                if (voyage == null) { req.Http.Response.StatusCode = 404; return new ApiErrorResponse { Error = ApiResultEnum.NotFound, Message = "Voyage not found" }; }

                // REST, WebSocket and MCP share one voyage cancel: every running captain is recalled first, then the
                // voyage and its live missions are written Cancelled, one voyage.cancelled event is written, and each
                // change is broadcast.
                VoyageCancellationResult cancellation = await _operations.CancelVoyageAsync(voyage).ConfigureAwait(false);
                voyage = cancellation.Voyage;

                return (object)new { Voyage = voyage, CancelledMissions = cancellation.CancelledMissions.Count };
            },
            api => api
                .WithTag("Voyages")
                .WithSummary("Cancel a voyage")
                .WithDescription("Cancels a voyage and every Pending, Assigned, InProgress, Testing or Review mission in it. The captain of each running mission is recalled first, which stops its agent process. A voyage.cancelled event is written and each change is broadcast; WebSocket cancel_voyage and MCP armada_cancel_voyage run the same cancel. A voyage that is already Cancelled or Complete is returned unchanged.")
                .WithParameter(OpenApiParameterMetadata.Path("id", "Voyage ID (vyg_ prefix)"))
                .WithResponse(404, OpenApiResponseMetadata.NotFound())
                .WithSecurity("ApiKey"));

            app.Delete("/api/v1/voyages/{id}/purge", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(ctx, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery))
                {
                    return RouteAuthRefusal.Refuse(req, ctx);
                }
                string id = req.Parameters["id"];
                Voyage? voyage = ctx.IsAdmin
                    ? await _database.Voyages.ReadAsync(id).ConfigureAwait(false)
                    : ctx.IsTenantAdmin
                        ? await _database.Voyages.ReadAsync(ctx.TenantId!, id).ConfigureAwait(false)
                        : await _database.Voyages.ReadAsync(ctx.TenantId!, ctx.UserId!, id).ConfigureAwait(false);
                if (voyage == null) { req.Http.Response.StatusCode = 404; return new ApiErrorResponse { Error = ApiResultEnum.NotFound, Message = "Voyage not found" }; }

                // REST, WebSocket and MCP share one voyage purge: the live-voyage and at-work refusals, and each
                // mission's guarded dock, worktree, log and diff removal.
                WorkPurgeResult purge = await _operations.PurgeVoyageAsync(
                    voyage,
                    voyageId => ctx.IsAdmin
                        ? _database.Missions.EnumerateByVoyageAsync(voyageId)
                        : _database.Missions.EnumerateByVoyageAsync(ctx.TenantId!, voyageId)).ConfigureAwait(false);
                if (!purge.Succeeded)
                {
                    req.Http.Response.StatusCode = 409;
                    return (object)new { Error = "Conflict", Message = purge.Message };
                }

                return (object)new { Status = "deleted", VoyageId = id, MissionsDeleted = purge.MissionsDeleted };
            },
            api => api
                .WithTag("Voyages")
                .WithSummary("Permanently delete a voyage")
                .WithDescription("Permanently deletes a voyage and all its missions, with each mission's dock record, worktree, log files and saved diff. This cannot be undone. Refused with 409 while the voyage is Open or InProgress or a captain is working one of its missions.")
                .WithParameter(OpenApiParameterMetadata.Path("id", "Voyage ID (vyg_ prefix)"))
                .WithResponse(200, OpenApiJson.For<object>("Deleted voyage and missions"))
                .WithResponse(404, OpenApiResponseMetadata.NotFound())
                .WithResponse(409, OpenApiJson.For<object>("Voyage cannot be deleted while active"))
                .WithSecurity("ApiKey"));

            app.Post<DeleteMultipleRequest>("/api/v1/voyages/delete/multiple", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(ctx, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery))
                {
                    return RouteAuthRefusal.Refuse(req, ctx);
                }
                DeleteMultipleRequest? body = JsonSerializer.Deserialize<DeleteMultipleRequest>(req.Http.Request.DataAsString, _jsonOptions);
                if (body == null || body.Ids == null || body.Ids.Count == 0)
                    return (object)new ApiErrorResponse { Error = ApiResultEnum.BadRequest, Message = "Ids is required and must not be empty" };

                DeleteMultipleResult result = await _operations.PurgeVoyagesAsync(
                    body.Ids,
                    voyageId => ctx.IsAdmin
                        ? _database.Voyages.ReadAsync(voyageId)
                        : ctx.IsTenantAdmin
                            ? _database.Voyages.ReadAsync(ctx.TenantId!, voyageId)
                            : _database.Voyages.ReadAsync(ctx.TenantId!, ctx.UserId!, voyageId),
                    voyageId => ctx.IsAdmin
                        ? _database.Missions.EnumerateByVoyageAsync(voyageId)
                        : _database.Missions.EnumerateByVoyageAsync(ctx.TenantId!, voyageId)).ConfigureAwait(false);
                return (object)result;
            },
            api => api
                .WithTag("Voyages")
                .WithSummary("Batch delete multiple voyages")
                .WithDescription("Permanently deletes multiple voyages by ID, each by the single-voyage purge rule. Voyages that are Open/InProgress or hold a mission a captain is working are skipped with their reason. Returns a summary of deleted and skipped entries. This cannot be undone.")
                .WithRequestBody(OpenApiJson.BodyFor<DeleteMultipleRequest>("List of voyage IDs to delete"))
                .WithResponse(200, OpenApiJson.For<DeleteMultipleResult>("Delete result summary"))
                .WithSecurity("ApiKey"));
        }
    }
}
