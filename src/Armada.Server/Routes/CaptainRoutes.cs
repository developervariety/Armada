namespace Armada.Server.Routes
{
    using System.Diagnostics;
    using System.IO;
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
    using Armada.Runtimes;
    using SyslogLogging;

    /// <summary>
    /// REST API routes for captain management.
    /// </summary>
    public class CaptainRoutes
    {
        private readonly DatabaseDriver _database;
        private readonly IAdmiralService _admiral;
        private readonly ArmadaSettings _settings;
        private readonly AgentRuntimeFactory _runtimeFactory;
        private readonly AgentLifecycleHandler _agentLifecycle;
        private readonly CaptainToolService _captainTools;
        private readonly Func<string, string, string?, string?, string?, string?, string?, string?, Task> _emitEvent;
        private readonly JsonSerializerOptions _jsonOptions;
        private readonly PlanningSessionCoordinator? _planningSessions;
        private readonly ObjectiveRefinementCoordinator? _objectiveRefinementSessions;
        private readonly LoggingModule? _Logging;
        private readonly ICaptainQuarantineService _captainQuarantine;
        private readonly CaptainAdministrationService _captainAdministration;
        private string _Header = "[CaptainRoutes] ";

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="admiral">Admiral coordination service.</param>
        /// <param name="settings">Application settings.</param>
        /// <param name="runtimeFactory">Agent runtime factory.</param>
        /// <param name="agentLifecycle">Agent lifecycle handler used for model validation.</param>
        /// <param name="captainTools">Captain tool availability service.</param>
        /// <param name="emitEvent">Event broadcast callback.</param>
        /// <param name="jsonOptions">JSON serializer options.</param>
        /// <param name="planningSessions">Optional planning session coordinator for captain-planning ownership handoff.</param>
        /// <param name="objectiveRefinementSessions">Optional objective refinement coordinator for captain-refinement ownership handoff.</param>
        /// <param name="logging">Optional logging module for structured warning output.</param>
        /// <param name="captainQuarantine">Shared quarantine service; the server passes the same instance MCP uses.</param>
        /// <param name="captainAdministration">Shared stop-all, deletion and restart service; the server passes the same instance MCP and WebSocket use.</param>
        public CaptainRoutes(
            DatabaseDriver database,
            IAdmiralService admiral,
            ArmadaSettings settings,
            AgentRuntimeFactory runtimeFactory,
            AgentLifecycleHandler agentLifecycle,
            CaptainToolService captainTools,
            Func<string, string, string?, string?, string?, string?, string?, string?, Task> emitEvent,
            JsonSerializerOptions jsonOptions,
            PlanningSessionCoordinator? planningSessions = null,
            ObjectiveRefinementCoordinator? objectiveRefinementSessions = null,
            LoggingModule? logging = null,
            ICaptainQuarantineService? captainQuarantine = null,
            CaptainAdministrationService? captainAdministration = null)
        {
            _captainQuarantine = captainQuarantine ?? new CaptainQuarantineService(database, settings, logging ?? new LoggingModule());
            _database = database;
            _admiral = admiral;
            _settings = settings;
            _runtimeFactory = runtimeFactory;
            _agentLifecycle = agentLifecycle;
            _captainTools = captainTools;
            _emitEvent = emitEvent;
            _jsonOptions = jsonOptions;
            _planningSessions = planningSessions;
            _objectiveRefinementSessions = objectiveRefinementSessions;
            _Logging = logging;
            if (captainAdministration == null)
            {
                captainAdministration = new CaptainAdministrationService(database, admiral.RecallCaptainAsync, logging);
                captainAdministration.StopProcess = agentLifecycle.HandleStopAgentAsync;
                captainAdministration.ValidateModel = agentLifecycle.ValidateCaptainModelAsync;
                captainAdministration.AttachSessionCoordinators(planningSessions, objectiveRefinementSessions);
            }
            _captainAdministration = captainAdministration;
        }

        /// <summary>
        /// Map a quarantine outcome to one HTTP status: 404 not found, 400 invalid, 409 busy, otherwise 200.
        /// </summary>
        private static object QuarantineResponse(ApiRequest req, CaptainQuarantineResult result)
        {
            switch (result.Outcome)
            {
                case CaptainQuarantineOutcomeEnum.NotFound:
                    req.Http.Response.StatusCode = 404;
                    return new ApiErrorResponse { Error = ApiResultEnum.NotFound, Message = result.Message };
                case CaptainQuarantineOutcomeEnum.InvalidRequest:
                    req.Http.Response.StatusCode = 400;
                    return new ApiErrorResponse { Error = ApiResultEnum.BadRequest, Message = result.Message };
                case CaptainQuarantineOutcomeEnum.Busy:
                    req.Http.Response.StatusCode = 409;
                    return result;
                default:
                    return result;
            }
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
            // Captains
            app.Get("/api/v1/captains", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(ctx, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery))
                {
                    return RouteAuthRefusal.Refuse(req, ctx);
                }
                EnumerationQuery query = new EnumerationQuery();
                query.ApplyQuerystringOverrides(key => QueryValueReader.Read(req, key));
                Stopwatch sw = Stopwatch.StartNew();
                EnumerationResult<Captain> result = ctx.IsAdmin
                    ? await _database.Captains.EnumerateAsync(query).ConfigureAwait(false)
                    : ctx.IsTenantAdmin
                        ? await _database.Captains.EnumerateAsync(ctx.TenantId!, query).ConfigureAwait(false)
                        : await _database.Captains.EnumerateAsync(ctx.TenantId!, ctx.UserId!, query).ConfigureAwait(false);
                result.TotalMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2);
                return result;
            },
            api => api
                .WithTag("Captains")
                .WithSummary("List all captains")
                .WithDescription("Returns all registered captains (AI agents) with their current state.")
                .WithResponse(200, OpenApiJson.For<EnumerationResult<Captain>>("Paginated captain list"))
                .WithSecurity("ApiKey"));

            app.Post<EnumerationQuery>("/api/v1/captains/enumerate", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(ctx, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery))
                {
                    return RouteAuthRefusal.Refuse(req, ctx);
                }
                EnumerationQuery query = JsonSerializer.Deserialize<EnumerationQuery>(req.Http.Request.DataAsString, _jsonOptions) ?? new EnumerationQuery();
                query.ApplyQuerystringOverrides(key => QueryValueReader.Read(req, key));
                Stopwatch sw = Stopwatch.StartNew();
                EnumerationResult<Captain> result = ctx.IsAdmin
                    ? await _database.Captains.EnumerateAsync(query).ConfigureAwait(false)
                    : ctx.IsTenantAdmin
                        ? await _database.Captains.EnumerateAsync(ctx.TenantId!, query).ConfigureAwait(false)
                        : await _database.Captains.EnumerateAsync(ctx.TenantId!, ctx.UserId!, query).ConfigureAwait(false);
                result.TotalMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2);
                return result;
            },
            api => api
                .WithTag("Captains")
                .WithSummary("Enumerate captains")
                .WithDescription("Paginated enumeration of captains with optional filtering and sorting.")
                .WithRequestBody(OpenApiJson.BodyFor<EnumerationQuery>("Enumeration query", false))
                .WithSecurity("ApiKey"));

            app.Post<Captain>("/api/v1/captains", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(ctx, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery))
                {
                    return RouteAuthRefusal.Refuse(req, ctx);
                }
                Captain input = JsonSerializer.Deserialize<Captain>(req.Http.Request.DataAsString, _jsonOptions)
                    ?? throw new InvalidOperationException("Request body could not be deserialized as Captain.");
                string? createOwnedFieldError = CaptainInputMapping.FindServerOwnedFieldViolation(
                    JsonSerializer.Deserialize<CaptainServerOwnedFields>(req.Http.Request.DataAsString, _jsonOptions), null);
                if (createOwnedFieldError != null)
                {
                    req.Http.Response.StatusCode = 400;
                    return new ApiErrorResponse { Error = ApiResultEnum.BadRequest, Message = createOwnedFieldError };
                }
                // REST, WebSocket and MCP share one captain create: the name rule, runtime-option normalization and
                // model validation (a credit or quota failure keeps the captain with a warning).
                CaptainWriteResult created = await _captainAdministration.CreateAsync(input, ctx.TenantId, ctx.UserId).ConfigureAwait(false);
                if (!created.Succeeded)
                {
                    bool conflict = created.Outcome == CaptainAdministrationOutcomeEnum.Busy;
                    req.Http.Response.StatusCode = conflict ? 409 : 400;
                    return new ApiErrorResponse { Error = conflict ? ApiResultEnum.Conflict : ApiResultEnum.BadRequest, Message = created.Message };
                }
                req.Http.Response.StatusCode = 201;
                return created.Captain!;
            },
            api => api
                .WithTag("Captains")
                .WithSummary("Create a captain")
                .WithDescription("Registers a new captain (AI agent). Accepts configuration fields only (Name, Runtime, Model, ModelEndpointId, ApiKey, ApiBaseUrl, SystemInstructions, AllowedPersonas, PreferredPersona, RuntimeOptionsJson, Tier, PreferenceRank, DefaultPlaybooks). A server-owned field (Id, TenantId, UserId, State, CurrentMissionId, CurrentDockId, ProcessId, RecoveryAttempts, LastHeartbeatUtc, LastProcessAliveUtc, QuarantineUntilUtc, QuarantineReason, CreatedUtc, LastUpdateUtc) sent with a non-default value returns 400 captain_server_owned_field naming the field. A name that another captain already has returns 409 Conflict and creates nothing.")
                .WithRequestBody(OpenApiJson.BodyFor<Captain>("Captain data", true))
                .WithResponse(201, OpenApiJson.For<Captain>("Created captain"))
                .WithResponse(400, OpenApiResponseMetadata.BadRequest())
                .WithResponse(409, OpenApiJson.For<ApiErrorResponse>("A captain with that name already exists"))
                .WithSecurity("ApiKey"));

            app.Get("/api/v1/captains/{id}", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(ctx, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery))
                {
                    return RouteAuthRefusal.Refuse(req, ctx);
                }
                string id = req.Parameters["id"];
                Captain? captain = ctx.IsAdmin
                    ? await _database.Captains.ReadAsync(id).ConfigureAwait(false)
                    : ctx.IsTenantAdmin
                        ? await _database.Captains.ReadAsync(ctx.TenantId!, id).ConfigureAwait(false)
                        : await _database.Captains.ReadAsync(ctx.TenantId!, ctx.UserId!, id).ConfigureAwait(false);
                if (captain == null) { req.Http.Response.StatusCode = 404; return new ApiErrorResponse { Error = ApiResultEnum.NotFound, Message = "Captain not found" }; }
                return (object)captain;
            },
            api => api
                .WithTag("Captains")
                .WithSummary("Get a captain")
                .WithDescription("Returns a single captain by ID.")
                .WithParameter(OpenApiParameterMetadata.Path("id", "Captain ID (cpt_ prefix)"))
                .WithResponse(200, OpenApiJson.For<Captain>("Captain details"))
                .WithResponse(404, OpenApiResponseMetadata.NotFound())
                .WithSecurity("ApiKey"));

            app.Get("/api/v1/captains/{id}/tools", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(ctx, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery))
                {
                    return RouteAuthRefusal.Refuse(req, ctx);
                }
                string id = req.Parameters["id"];
                Captain? captain = ctx.IsAdmin
                    ? await _database.Captains.ReadAsync(id).ConfigureAwait(false)
                    : ctx.IsTenantAdmin
                        ? await _database.Captains.ReadAsync(ctx.TenantId!, id).ConfigureAwait(false)
                        : await _database.Captains.ReadAsync(ctx.TenantId!, ctx.UserId!, id).ConfigureAwait(false);
                if (captain == null) { req.Http.Response.StatusCode = 404; return new ApiErrorResponse { Error = ApiResultEnum.NotFound, Message = "Captain not found" }; }
                string context = QueryValueReader.Read(req, "context") ?? String.Empty;
                if (!String.IsNullOrWhiteSpace(context) && !String.Equals(context, "ask", StringComparison.OrdinalIgnoreCase))
                {
                    req.Http.Response.StatusCode = 400;
                    return new ApiErrorResponse { Error = ApiResultEnum.BadRequest, Message = "Unknown tools context." };
                }
                return await _captainTools.DescribeAsync(captain, plannedAsk: String.Equals(context, "ask", StringComparison.OrdinalIgnoreCase), caller: ctx).ConfigureAwait(false);
            },
            api => api
                .WithTag("Captains")
                .WithSummary("Describe captain tools")
                .WithDescription("Returns the runtime-visible tool sources and named tools available to the selected captain, including runtime-specific availability notes.")
                .WithParameter(OpenApiParameterMetadata.Path("id", "Captain ID (cpt_ prefix)"))
                .WithResponse(200, OpenApiJson.For<CaptainToolAccessResult>("Captain runtime tool availability and inventory"))
                .WithResponse(404, OpenApiResponseMetadata.NotFound())
                .WithSecurity("ApiKey"));

            app.Put<Captain>("/api/v1/captains/{id}", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(ctx, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery))
                {
                    return RouteAuthRefusal.Refuse(req, ctx);
                }
                string id = req.Parameters["id"];
                Captain? existing = ctx.IsAdmin
                    ? await _database.Captains.ReadAsync(id).ConfigureAwait(false)
                    : ctx.IsTenantAdmin
                        ? await _database.Captains.ReadAsync(ctx.TenantId!, id).ConfigureAwait(false)
                        : await _database.Captains.ReadAsync(ctx.TenantId!, ctx.UserId!, id).ConfigureAwait(false);
                if (existing == null) { req.Http.Response.StatusCode = 404; return new ApiErrorResponse { Error = ApiResultEnum.NotFound, Message = "Captain not found" }; }
                Captain input = JsonSerializer.Deserialize<Captain>(req.Http.Request.DataAsString, _jsonOptions)
                    ?? throw new InvalidOperationException("Request body could not be deserialized as Captain.");
                string? updateOwnedFieldError = CaptainInputMapping.FindServerOwnedFieldViolation(
                    JsonSerializer.Deserialize<CaptainServerOwnedFields>(req.Http.Request.DataAsString, _jsonOptions), existing);
                if (updateOwnedFieldError != null)
                {
                    req.Http.Response.StatusCode = 400;
                    return new ApiErrorResponse { Error = ApiResultEnum.BadRequest, Message = updateOwnedFieldError };
                }
                // REST, WebSocket and MCP share one captain update: runtime-option normalization and, when the runtime,
                // model, endpoint or credentials change, model validation.
                CaptainWriteResult updated = await _captainAdministration.UpdateAsync(existing, input).ConfigureAwait(false);
                if (!updated.Succeeded)
                {
                    req.Http.Response.StatusCode = 400;
                    return new ApiErrorResponse { Error = ApiResultEnum.BadRequest, Message = updated.Message };
                }
                return (object)updated.Captain!;
            },
            api => api
                .WithTag("Captains")
                .WithSummary("Update a captain")
                .WithDescription("Replaces a captain's configuration fields (Name, Runtime, Model, ModelEndpointId, ApiKey, ApiBaseUrl, SystemInstructions, AllowedPersonas, PreferredPersona, RuntimeOptionsJson, Tier, PreferenceRank, DefaultPlaybooks). Server-owned fields keep their stored values; sending one with a different value returns 400 captain_server_owned_field naming the field.")
                .WithParameter(OpenApiParameterMetadata.Path("id", "Captain ID (cpt_ prefix)"))
                .WithRequestBody(OpenApiJson.BodyFor<Captain>("Updated captain data", true))
                .WithResponse(200, OpenApiJson.For<Captain>("Updated captain"))
                .WithResponse(400, OpenApiResponseMetadata.BadRequest())
                .WithResponse(404, OpenApiResponseMetadata.NotFound())
                .WithSecurity("ApiKey"));

            app.Post("/api/v1/captains/{id}/stop", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(ctx, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery))
                {
                    return RouteAuthRefusal.Refuse(req, ctx);
                }
                CaptainStopResult stopped = await _captainAdministration.StopAsync(req.Parameters["id"], ctx).ConfigureAwait(false);
                switch (stopped.Outcome)
                {
                    case CaptainAdministrationOutcomeEnum.NotFound:
                        req.Http.Response.StatusCode = 404;
                        return new ApiErrorResponse { Error = ApiResultEnum.NotFound, Message = stopped.Message };
                    case CaptainAdministrationOutcomeEnum.Conflict:
                        req.Http.Response.StatusCode = 409;
                        return new ApiErrorResponse { Error = ApiResultEnum.Conflict, Message = stopped.Message };
                    case CaptainAdministrationOutcomeEnum.Failed:
                        req.Http.Response.StatusCode = 500;
                        return new ApiErrorResponse { Error = ApiResultEnum.InternalError, Message = stopped.Message };
                    default:
                        return stopped;
                }
            },
            api => api
                .WithTag("Captains")
                .WithSummary("Stop a captain")
                .WithDescription("Stops a running captain agent, killing its process and recalling it to idle state.")
                .WithParameter(OpenApiParameterMetadata.Path("id", "Captain ID (cpt_ prefix)"))
                .WithResponse(404, OpenApiResponseMetadata.NotFound())
                .WithSecurity("ApiKey"));

            app.Post("/api/v1/captains/{id}/unquarantine", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(ctx, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery))
                {
                    return RouteAuthRefusal.Refuse(req, ctx);
                }
                string uqId = req.Parameters["id"];
                CaptainQuarantineResult released = await _captainQuarantine.ReleaseCaptainAsync(ctx, uqId).ConfigureAwait(false);
                return QuarantineResponse(req, released);
            },
            api => api
                .WithTag("Captains")
                .WithSummary("Lift a captain's quarantine")
                .WithDescription("Releases a quarantined captain to Idle through the shared quarantine service. A captain that is not quarantined is left unchanged and reported as NotQuarantined.")
                .WithParameter(OpenApiParameterMetadata.Path("id", "Captain ID (cpt_ prefix)"))
                .WithResponse(200, OpenApiJson.For<CaptainQuarantineResult>("Quarantine release outcome"))
                .WithResponse(404, OpenApiResponseMetadata.NotFound())
                .WithSecurity("ApiKey"));

            app.Post("/api/v1/captains/{id}/quarantine", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(ctx, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery))
                {
                    return RouteAuthRefusal.Refuse(req, ctx);
                }

                CaptainQuarantineRequest? body = null;
                try
                {
                    string raw = req.Http.Request.DataAsString;
                    body = String.IsNullOrWhiteSpace(raw) ? null : JsonSerializer.Deserialize<CaptainQuarantineRequest>(raw, _jsonOptions);
                }
                catch (JsonException)
                {
                    req.Http.Response.StatusCode = 400;
                    return new ApiErrorResponse { Error = ApiResultEnum.BadRequest, Message = "Request body is not valid JSON" };
                }

                if (body?.DurationMinutes != null && body.DurationMinutes.Value <= 0)
                {
                    req.Http.Response.StatusCode = 400;
                    return new ApiErrorResponse { Error = ApiResultEnum.BadRequest, Message = "DurationMinutes must be positive" };
                }

                DateTime? untilUtc = body?.UntilUtc;
                if (!untilUtc.HasValue && body?.DurationMinutes != null)
                    untilUtc = DateTime.UtcNow.AddMinutes(body.DurationMinutes.Value);

                CaptainQuarantineResult quarantined = await _captainQuarantine.QuarantineCaptainAsync(
                    ctx, req.Parameters["id"], body?.Reason, untilUtc).ConfigureAwait(false);
                return QuarantineResponse(req, quarantined);
            },
            api => api
                .WithTag("Captains")
                .WithSummary("Quarantine a captain")
                .WithDescription("Holds a captain out of assignment with a required reason and an optional expiry (UntilUtc, or DurationMinutes; neither is an indefinite hold). The write succeeds only while the captain is Idle or already quarantined and owns no mission, dock or process; otherwise it returns 409 with outcome Busy and nothing changes.")
                .WithParameter(OpenApiParameterMetadata.Path("id", "Captain ID (cpt_ prefix)"))
                .WithRequestBody(OpenApiJson.BodyFor<CaptainQuarantineRequest>("Quarantine request", true))
                .WithResponse(200, OpenApiJson.For<CaptainQuarantineResult>("Quarantine outcome"))
                .WithResponse(409, OpenApiJson.For<CaptainQuarantineResult>("Refused: the captain owns work or is not Idle"))
                .WithResponse(400, OpenApiResponseMetadata.BadRequest())
                .WithResponse(404, OpenApiResponseMetadata.NotFound())
                .WithSecurity("ApiKey"));

            app.Post("/api/v1/captains/stop-all", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(ctx, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery))
                {
                    return RouteAuthRefusal.Refuse(req, ctx);
                }
                CaptainStopAllResult stopAll = await _captainAdministration.StopAllAsync().ConfigureAwait(false);
                return (object)stopAll;
            },
            api => api
                .WithTag("Captains")
                .WithSummary("Stop all captains")
                .WithDescription("Emergency stop of every working captain, active planning session and active objective refinement session. Each stop is attempted independently. Status is all_stopped when every stop succeeded and stopped_with_failures otherwise; the result counts stopped and failed captains and sessions and names each failure.")
                .WithResponse(200, OpenApiJson.For<CaptainStopAllResult>("Stopped and failed counts"))
                .WithSecurity("ApiKey"));

            app.Post("/api/v1/captains/{id}/restart", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(ctx, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery))
                {
                    return RouteAuthRefusal.Refuse(req, ctx);
                }
                CaptainRestartResult restarted = await _captainAdministration.RestartAsync(req.Parameters["id"], ctx).ConfigureAwait(false);
                switch (restarted.Outcome)
                {
                    case CaptainAdministrationOutcomeEnum.NotFound:
                        req.Http.Response.StatusCode = 404;
                        return new ApiErrorResponse { Error = ApiResultEnum.NotFound, Message = restarted.Message };
                    case CaptainAdministrationOutcomeEnum.Busy:
                        req.Http.Response.StatusCode = 409;
                        return (object)new { Error = "Conflict", Message = restarted.Message };
                    case CaptainAdministrationOutcomeEnum.Failed:
                        req.Http.Response.StatusCode = 500;
                        return (object)new { Error = "RestartFailed", Message = restarted.Message };
                    default:
                        return (object)restarted.Captain!;
                }
            },
            api => api
                .WithTag("Captains")
                .WithSummary("Restart a captain in place")
                .WithDescription("Resets a captain's runtime state without replacing the record. The identifier, configuration, credentials, endpoint, base URL, playbooks, ownership and any quarantine or bench hold are kept. A leftover agent process is stopped; the mission, dock and process references and the recovery count are cleared; a captain without a hold returns to Idle. Refused with 409 while the captain is Working, Planning or Refining or owns an Assigned or InProgress mission. A refused or failed restart leaves the captain unchanged.")
                .WithParameter(OpenApiParameterMetadata.Path("id", "Captain ID (cpt_ prefix)"))
                .WithResponse(200, OpenApiJson.For<Captain>("Restarted captain"))
                .WithResponse(404, OpenApiResponseMetadata.NotFound())
                .WithResponse(409, OpenApiJson.For<object>("Captain is busy"))
                .WithSecurity("ApiKey"));

            app.Get("/api/v1/captains/{id}/log", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(ctx, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery))
                {
                    return RouteAuthRefusal.Refuse(req, ctx);
                }
                string id = req.Parameters["id"];
                Captain? captain = ctx.IsAdmin
                    ? await _database.Captains.ReadAsync(id).ConfigureAwait(false)
                    : ctx.IsTenantAdmin
                        ? await _database.Captains.ReadAsync(ctx.TenantId!, id).ConfigureAwait(false)
                        : await _database.Captains.ReadAsync(ctx.TenantId!, ctx.UserId!, id).ConfigureAwait(false);
                if (captain == null) { req.Http.Response.StatusCode = 404; return new ApiErrorResponse { Error = ApiResultEnum.NotFound, Message = "Captain not found" }; }

                bool formatted = String.Equals(QueryValueReader.Read(req, "formatted"), "true", StringComparison.OrdinalIgnoreCase);
                int? offset = null;
                int? lineCount = null;
                string? offsetParam = QueryValueReader.Read(req, "offset");
                if (!String.IsNullOrEmpty(offsetParam) && Int32.TryParse(offsetParam, out int parsedOffset)) offset = parsedOffset;
                string? linesParam = QueryValueReader.Read(req, "lines");
                if (!String.IsNullOrEmpty(linesParam) && Int32.TryParse(linesParam, out int parsedLines)) lineCount = parsedLines;

                // ?formatted=true applies the readable formatter: resolves tool names out of runtime
                // JSONL, redacts secret-shaped values, truncates oversized payloads, and drops noise.
                return await SessionLogReader.ReadCaptainLogAsync(_settings.LogDirectory, captain, offset, lineCount, 50, formatted).ConfigureAwait(false);
            },
            api => api
                .WithTag("Captains")
                .WithSummary("Get current log for a captain")
                .WithDescription("Returns the current session log for a captain, resolved via the .current pointer file. Supports pagination via ?lines=N (default 50) and ?offset=N.")
                .WithParameter(OpenApiParameterMetadata.Path("id", "Captain ID (cpt_ prefix)"))
                .WithResponse(404, OpenApiResponseMetadata.NotFound())
                .WithSecurity("ApiKey"));

            app.Delete("/api/v1/captains/{id}", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(ctx, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery))
                {
                    return RouteAuthRefusal.Refuse(req, ctx);
                }
                CaptainDeletionResult deletion = await _captainAdministration.DeleteAsync(req.Parameters["id"], ctx).ConfigureAwait(false);
                if (deletion.Outcome == CaptainAdministrationOutcomeEnum.NotFound)
                {
                    req.Http.Response.StatusCode = 404;
                    return new ApiErrorResponse { Error = ApiResultEnum.NotFound, Message = deletion.Message };
                }
                if (deletion.Outcome == CaptainAdministrationOutcomeEnum.Busy)
                {
                    req.Http.Response.StatusCode = 409;
                    return (object)new { Error = "Conflict", Message = deletion.Message };
                }

                req.Http.Response.StatusCode = 204;
                return null;
            },
            api => api
                .WithTag("Captains")
                .WithSummary("Delete a captain")
                .WithDescription("Deletes a captain and the events, planning sessions and objective refinement sessions that reference it. Refused with 409 while the captain is Working, Planning or Refining or owns an Assigned or InProgress mission.")
                .WithParameter(OpenApiParameterMetadata.Path("id", "Captain ID (cpt_ prefix)"))
                .WithResponse(204, OpenApiResponseMetadata.NoContent())
                .WithResponse(404, OpenApiResponseMetadata.NotFound())
                .WithResponse(409, OpenApiJson.For<object>("Captain cannot be deleted while active"))
                .WithSecurity("ApiKey"));

            app.Post<DeleteMultipleRequest>("/api/v1/captains/delete/multiple", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(ctx, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery))
                {
                    return RouteAuthRefusal.Refuse(req, ctx);
                }
                DeleteMultipleRequest? body = JsonSerializer.Deserialize<DeleteMultipleRequest>(req.Http.Request.DataAsString, _jsonOptions);
                if (body == null || body.Ids == null || body.Ids.Count == 0)
                    return (object)new ApiErrorResponse { Error = ApiResultEnum.BadRequest, Message = "Ids is required and must not be empty" };

                DeleteMultipleResult result = await _captainAdministration.DeleteManyAsync(body.Ids, ctx).ConfigureAwait(false);

                await _emitEvent("captain.batch_deleted", "Batch deleted " + result.Deleted + " captains",
                    "captain", null, null, null, null, null).ConfigureAwait(false);

                return (object)result;
            },
            api => api
                .WithTag("Captains")
                .WithSummary("Batch delete multiple captains")
                .WithDescription("Permanently deletes multiple captains by ID with the same rule and dependent cleanup as a single delete. Captains that are Working, Planning or Refining or own an Assigned or InProgress mission are skipped. Returns a summary of deleted and skipped entries. This cannot be undone.")
                .WithRequestBody(OpenApiJson.BodyFor<DeleteMultipleRequest>("List of captain IDs to delete"))
                .WithResponse(200, OpenApiJson.For<DeleteMultipleResult>("Delete result summary"))
                .WithSecurity("ApiKey"));
        }
    }
}
