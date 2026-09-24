namespace Armada.Server.Routes
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
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
    using Armada.Server.WebSocket;
    using SyslogLogging;

    /// <summary>
    /// REST API routes for mission management.
    /// </summary>
    public class MissionRoutes
    {
        private readonly DatabaseDriver _database;
        private readonly IAdmiralService _admiral;
        private readonly IMissionService _missionService;
        private readonly MissionStatusTransitionService _statusTransitions;
        private readonly ArmadaSettings _settings;
        private readonly IGitService _git;
        private readonly ILandingService _landingService;
        private readonly LandingPreviewService _landingPreview;
        private readonly DefinitionOfDoneReportService _definitionOfDoneReport;
        private readonly MissionRecoveryReportService _recoveryReport;
        private readonly MissionAutoLandReportService _autoLandReport;
        private readonly GitHubIntegrationService _gitHub;
        private readonly Func<string, string, string?, string?, string?, string?, string?, string?, Task> _emitEvent;
        private readonly ArmadaWebSocketHub? _webSocketHub;
        private readonly LoggingModule _logging;
        private readonly JsonSerializerOptions _jsonOptions;
        private readonly MissionOperations _operations;

        private sealed class MissionInstructionsPath
        {
            public string FileName { get; set; } = String.Empty;
            public string Path { get; set; } = String.Empty;
        }

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="admiral">Admiral coordination service.</param>
        /// <param name="missionService">Mission lifecycle service.</param>
        /// <param name="settings">Application settings.</param>
        /// <param name="git">Git operations service.</param>
        /// <param name="landingService">Mission landing service.</param>
        /// <param name="landingPreview">Mission landing-preview service.</param>
        /// <param name="gitHub">GitHub integration service.</param>
        /// <param name="emitEvent">Event broadcast callback.</param>
        /// <param name="webSocketHub">WebSocket hub for real-time notifications.</param>
        /// <param name="logging">Logging module.</param>
        /// <param name="jsonOptions">JSON serializer options.</param>
        /// <param name="statusTransitions">Shared operator status transition path.</param>
        /// <param name="operations">Shared mission and voyage operations. When null, one is built from these
        /// dependencies that writes events through <paramref name="emitEvent"/> and broadcasts through the hub.</param>
        public MissionRoutes(
            DatabaseDriver database,
            IAdmiralService admiral,
            IMissionService missionService,
            ArmadaSettings settings,
            IGitService git,
            ILandingService landingService,
            LandingPreviewService landingPreview,
            GitHubIntegrationService gitHub,
            Func<string, string, string?, string?, string?, string?, string?, string?, Task> emitEvent,
            ArmadaWebSocketHub? webSocketHub,
            LoggingModule logging,
            JsonSerializerOptions jsonOptions,
            MissionStatusTransitionService statusTransitions,
            MissionOperations? operations = null)
        {
            _database = database;
            _admiral = admiral;
            _missionService = missionService;
            _statusTransitions = statusTransitions ?? throw new ArgumentNullException(nameof(statusTransitions));
            _settings = settings;
            _git = git;
            _landingService = landingService;
            _landingPreview = landingPreview ?? throw new ArgumentNullException(nameof(landingPreview));
            _definitionOfDoneReport = new DefinitionOfDoneReportService(
                database,
                logging,
                () => (missionService as MissionService)?.DefinitionOfDone);
            _recoveryReport = new MissionRecoveryReportService(database, logging, settings);
            _autoLandReport = new MissionAutoLandReportService(database, logging);
            _gitHub = gitHub ?? throw new ArgumentNullException(nameof(gitHub));
            _emitEvent = emitEvent;
            _webSocketHub = webSocketHub;
            _logging = logging;
            _jsonOptions = jsonOptions;
            _operations = operations ?? new MissionOperations(
                database,
                settings,
                new DockService(logging, database, settings, git),
                (captainId, token) => admiral.RecallCaptainAsync(captainId, token),
                new OperationNotifier(emitEvent, mission => webSocketHub?.BroadcastMissionChange(mission), voyage => webSocketHub?.BroadcastVoyageChange(voyage)),
                logging);
        }

        private async Task<string> ReadFileSharedAsync(string path)
        {
            using FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using StreamReader reader = new StreamReader(fs);
            return await reader.ReadToEndAsync().ConfigureAwait(false);
        }

        private async Task<MissionInstructionsPath?> ResolveMissionInstructionsPathAsync(AuthContext ctx, Mission mission)
        {
            Captain? captain = null;
            if (!String.IsNullOrEmpty(mission.CaptainId))
            {
                captain = ctx.IsAdmin
                    ? await _database.Captains.ReadAsync(mission.CaptainId).ConfigureAwait(false)
                    : ctx.IsTenantAdmin
                        ? await _database.Captains.ReadAsync(ctx.TenantId!, mission.CaptainId).ConfigureAwait(false)
                        : await _database.Captains.ReadAsync(ctx.TenantId!, ctx.UserId!, mission.CaptainId).ConfigureAwait(false);
            }

            Dock? dock = null;
            string? dockId = mission.DockId ?? captain?.CurrentDockId;
            if (!String.IsNullOrEmpty(dockId))
            {
                dock = ctx.IsAdmin
                    ? await _database.Docks.ReadAsync(dockId).ConfigureAwait(false)
                    : ctx.IsTenantAdmin
                        ? await _database.Docks.ReadAsync(ctx.TenantId!, dockId).ConfigureAwait(false)
                        : await _database.Docks.ReadAsync(ctx.TenantId!, ctx.UserId!, dockId).ConfigureAwait(false);
            }

            if (dock == null || String.IsNullOrEmpty(dock.WorktreePath) || !Directory.Exists(dock.WorktreePath))
                return null;

            string? runtimeName = captain != null ? captain.Runtime.ToString() : null;
            string fileName = MissionPromptBuilder.GetInstructionsFileName(runtimeName);
            string generatedRelativePath = MissionPromptBuilder.GetGeneratedInstructionsRelativePath(runtimeName);
            string path = Path.Combine(dock.WorktreePath, generatedRelativePath);
            if (File.Exists(path))
            {
                return new MissionInstructionsPath
                {
                    FileName = generatedRelativePath,
                    Path = path
                };
            }

            path = Path.Combine(dock.WorktreePath, fileName);
            if (File.Exists(path))
            {
                return new MissionInstructionsPath
                {
                    FileName = fileName,
                    Path = path
                };
            }

            string[] fallbackNames = { "CLAUDE.md", "CODEX.md", "CURSOR.md", "AGENTS.md", "GEMINI.md", "MUX.md" };
            foreach (string fallbackName in fallbackNames)
            {
                string fallbackPath = Path.Combine(dock.WorktreePath, fallbackName);
                if (File.Exists(fallbackPath))
                {
                    return new MissionInstructionsPath
                    {
                        FileName = fallbackName,
                        Path = fallbackPath
                    };
                }
            }

            return new MissionInstructionsPath
            {
                FileName = fileName,
                Path = path
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

            // Missions
            app.Get("/api/v1/missions", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(ctx, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery))
                {
                    return RouteAuthRefusal.Refuse(req, ctx);
                }
                EnumerationQuery query = new EnumerationQuery();
                query.ApplyQuerystringOverrides(key => QueryValueReader.Read(req, key));
                Stopwatch sw = Stopwatch.StartNew();
                EnumerationResult<Mission> result = ctx.IsAdmin
                    ? await _database.Missions.EnumerateSummariesAsync(query).ConfigureAwait(false)
                    : ctx.IsTenantAdmin
                        ? await _database.Missions.EnumerateSummariesAsync(ctx.TenantId!, query).ConfigureAwait(false)
                        : await _database.Missions.EnumerateSummariesAsync(ctx.TenantId!, ctx.UserId!, query).ConfigureAwait(false);
                result.TotalMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2);
                foreach (Mission m in result.Objects)
                {
                    m.DiffSnapshot = null;
                    m.Description = null;
                    m.AgentOutput = null;
                    m.PlaybookSnapshots = new List<MissionPlaybookSnapshot>();
                }
                return result;
            },
            api => api
                .WithTag("Missions")
                .WithSummary("List all missions")
                .WithDescription("Returns all missions, filterable by status, vesselId, captainId, or voyageId.")
                .WithParameter(OpenApiParameterMetadata.Query("status", "Filter by mission status (Pending, Assigned, InProgress, WorkProduced, Testing, Review, Complete, Failed, LandingFailed, Cancelled)", false))
                .WithParameter(OpenApiParameterMetadata.Query("vesselId", "Filter by vessel ID", false))
                .WithParameter(OpenApiParameterMetadata.Query("captainId", "Filter by captain ID", false))
                .WithParameter(OpenApiParameterMetadata.Query("voyageId", "Filter by voyage ID", false))
                .WithResponse(200, OpenApiJson.For<EnumerationResult<Mission>>("Paginated mission list"))
                .WithSecurity("ApiKey"));

            app.Post<EnumerationQuery>("/api/v1/missions/enumerate", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(ctx, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery))
                {
                    return RouteAuthRefusal.Refuse(req, ctx);
                }
                EnumerationQuery query = JsonSerializer.Deserialize<EnumerationQuery>(req.Http.Request.DataAsString, _jsonOptions) ?? new EnumerationQuery();
                query.ApplyQuerystringOverrides(key => QueryValueReader.Read(req, key));
                Stopwatch sw = Stopwatch.StartNew();
                EnumerationResult<Mission> result = ctx.IsAdmin
                    ? await _database.Missions.EnumerateSummariesAsync(query).ConfigureAwait(false)
                    : ctx.IsTenantAdmin
                        ? await _database.Missions.EnumerateSummariesAsync(ctx.TenantId!, query).ConfigureAwait(false)
                        : await _database.Missions.EnumerateSummariesAsync(ctx.TenantId!, ctx.UserId!, query).ConfigureAwait(false);
                result.TotalMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2);
                foreach (Mission m in result.Objects)
                {
                    m.DiffSnapshot = null;
                    m.Description = null;
                    m.AgentOutput = null;
                    m.PlaybookSnapshots = new List<MissionPlaybookSnapshot>();
                }
                return result;
            },
            api => api
                .WithTag("Missions")
                .WithSummary("Enumerate missions")
                .WithDescription("Paginated enumeration of missions with optional filtering and sorting.")
                .WithRequestBody(OpenApiJson.BodyFor<EnumerationQuery>("Enumeration query", false))
                .WithSecurity("ApiKey"));

            app.Get("/api/v1/missions/summaries", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(ctx, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery))
                {
                    return RouteAuthRefusal.Refuse(req, ctx);
                }
                EnumerationQuery query = new EnumerationQuery();
                query.ApplyQuerystringOverrides(key => QueryValueReader.Read(req, key));
                return await MissionSummaryQuery.EnumerateForCallerAsync(_database, ctx, query).ConfigureAwait(false);
            },
            api => api
                .WithTag("Missions")
                .WithSummary("List lightweight mission summaries")
                .WithDescription("Returns lightweight mission summaries without large description, diff, or agent-output payloads.")
                .WithParameter(OpenApiParameterMetadata.Query("status", "Filter by mission status", false))
                .WithParameter(OpenApiParameterMetadata.Query("vesselId", "Filter by vessel ID", false))
                .WithParameter(OpenApiParameterMetadata.Query("captainId", "Filter by captain ID", false))
                .WithParameter(OpenApiParameterMetadata.Query("voyageId", "Filter by voyage ID", false))
                .WithResponse(200, OpenApiJson.For<EnumerationResult<MissionSummary>>("Paginated mission summary list"))
                .WithSecurity("ApiKey"));

            app.Post<EnumerationQuery>("/api/v1/missions/summaries/enumerate", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(ctx, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery))
                {
                    return RouteAuthRefusal.Refuse(req, ctx);
                }
                EnumerationQuery query = JsonSerializer.Deserialize<EnumerationQuery>(req.Http.Request.DataAsString, _jsonOptions) ?? new EnumerationQuery();
                query.ApplyQuerystringOverrides(key => QueryValueReader.Read(req, key));
                return await MissionSummaryQuery.EnumerateForCallerAsync(_database, ctx, query).ConfigureAwait(false);
            },
            api => api
                .WithTag("Missions")
                .WithSummary("Enumerate lightweight mission summaries")
                .WithDescription("Paginated enumeration of lightweight mission summaries with optional filtering and sorting.")
                .WithRequestBody(OpenApiJson.BodyFor<EnumerationQuery>("Enumeration query", false))
                .WithResponse(200, OpenApiJson.For<EnumerationResult<MissionSummary>>("Paginated mission summary list"))
                .WithSecurity("ApiKey"));

            app.Get("/api/v1/missions/history", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(ctx, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery))
                {
                    return RouteAuthRefusal.Refuse(req, ctx);
                }

                MissionHistoryQuery query = new MissionHistoryQuery();
                if (DateTime.TryParse(QueryValueReader.Read(req, "fromUtc"), out DateTime fromUtc)) query.FromUtc = fromUtc.ToUniversalTime();
                if (DateTime.TryParse(QueryValueReader.Read(req, "toUtc"), out DateTime toUtc)) query.ToUtc = toUtc.ToUniversalTime();
                if (int.TryParse(QueryValueReader.Read(req, "bucketMinutes"), out int bucketMinutes) && bucketMinutes > 0) query.BucketMinutes = bucketMinutes;
                string? fleetId = QueryValueReader.Read(req, "fleetId");
                if (!String.IsNullOrEmpty(fleetId)) query.FleetId = fleetId;
                string? vesselId = QueryValueReader.Read(req, "vesselId");
                if (!String.IsNullOrEmpty(vesselId)) query.VesselId = vesselId;

                List<MissionHistoryPoint> points = ctx.IsAdmin
                    ? await _database.Missions.EnumerateHistoryPointsAsync(query).ConfigureAwait(false)
                    : ctx.IsTenantAdmin
                        ? await _database.Missions.EnumerateHistoryPointsAsync(ctx.TenantId!, query).ConfigureAwait(false)
                        : await _database.Missions.EnumerateHistoryPointsAsync(ctx.TenantId!, ctx.UserId!, query).ConfigureAwait(false);

                if (!String.IsNullOrEmpty(query.FleetId))
                {
                    List<Vessel> vessels = ctx.IsAdmin
                        ? await _database.Vessels.EnumerateAsync().ConfigureAwait(false)
                        : ctx.IsTenantAdmin
                            ? await _database.Vessels.EnumerateAsync(ctx.TenantId!).ConfigureAwait(false)
                            : await _database.Vessels.EnumerateAsync(ctx.TenantId!, ctx.UserId!).ConfigureAwait(false);
                    HashSet<string> allowedVesselIds = vessels
                        .Where(v => String.Equals(v.FleetId, query.FleetId, StringComparison.Ordinal))
                        .Select(v => v.Id)
                        .ToHashSet(StringComparer.Ordinal);
                    points = points
                        .Where(point => !String.IsNullOrEmpty(point.VesselId) && allowedVesselIds.Contains(point.VesselId!))
                        .ToList();
                }

                return BuildMissionHistorySummary(query, points);
            },
            api => api
                .WithTag("Missions")
                .WithSummary("Get aggregated mission history")
                .WithDescription("Returns aggregated mission counts by time bucket for dashboard history charts.")
                .WithParameter(OpenApiParameterMetadata.Query("fromUtc", "Inclusive UTC start time", false))
                .WithParameter(OpenApiParameterMetadata.Query("toUtc", "Exclusive UTC end time", false))
                .WithParameter(OpenApiParameterMetadata.Query("bucketMinutes", "Bucket size in minutes", false))
                .WithParameter(OpenApiParameterMetadata.Query("fleetId", "Optional fleet filter", false))
                .WithParameter(OpenApiParameterMetadata.Query("vesselId", "Optional vessel filter", false))
                .WithResponse(200, OpenApiJson.For<MissionHistorySummaryResult>("Mission history summary"))
                .WithSecurity("ApiKey"));

            app.Post<Mission>("/api/v1/missions", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(ctx, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery))
                {
                    return RouteAuthRefusal.Refuse(req, ctx);
                }
                Mission mission = JsonSerializer.Deserialize<Mission>(req.Http.Request.DataAsString, _jsonOptions)
                    ?? throw new InvalidOperationException("Request body could not be deserialized as Mission.");
                mission.TenantId = ctx.TenantId;
                mission.UserId = ctx.UserId;
                string? unreachable = await MissionReferenceScope.FindUnreachableOnCreateAsync(_database, ctx, mission).ConfigureAwait(false);
                if (unreachable != null)
                {
                    req.Http.Response.StatusCode = 404;
                    return new ApiErrorResponse { Error = ApiResultEnum.NotFound, Message = unreachable };
                }
                await MissionDefaultPlaybooks.MergeVesselDefaultsAsync(_database, mission).ConfigureAwait(false);
                try
                {
                    mission = await _admiral.DispatchMissionAsync(mission).ConfigureAwait(false);
                }
                catch (DispatchHoldActiveException held)
                {
                    req.Http.Response.StatusCode = 409;
                    return DispatchHoldRefusal.From(held.Hold);
                }
                catch (FleetCapacityAdmissionException capacity)
                {
                    req.Http.Response.StatusCode = 409;
                    return new
                    {
                        Error = capacity.Message,
                        capacity.Code,
                        capacity.ActiveCount,
                        capacity.Limit,
                        capacity.CandidateVesselId,
                        capacity.LaneMembers
                    };
                }
                req.Http.Response.StatusCode = 201;
                if (mission.Status == MissionStatusEnum.Pending)
                {
                    return (object)new
                    {
                        Mission = mission,
                        Warning = "Mission created but could not be assigned to any captain. It will be retried on the next health check cycle."
                    };
                }
                return mission;
            },
            api => api
                .WithTag("Missions")
                .WithSummary("Create a mission")
                .WithDescription("Creates and dispatches a new mission. If a vesselId is provided, the Admiral will assign a captain and set up a worktree. Every id the body names (vesselId, voyageId, captainId, requestedCaptainId, dependsOnMissionId, parentMissionId) must be visible to the caller; otherwise 404 and nothing is created.")
                .WithRequestBody(OpenApiJson.BodyFor<Mission>("Mission data", true))
                .WithResponse(201, OpenApiJson.For<Mission>("Created mission"))
                .WithSecurity("ApiKey"));

            app.Get("/api/v1/missions/{id}", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(ctx, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery))
                {
                    return RouteAuthRefusal.Refuse(req, ctx);
                }
                string id = req.Parameters["id"];
                Mission? mission = ctx.IsAdmin
                    ? await _database.Missions.ReadAsync(id).ConfigureAwait(false)
                    : ctx.IsTenantAdmin
                        ? await _database.Missions.ReadAsync(ctx.TenantId!, id).ConfigureAwait(false)
                        : await _database.Missions.ReadAsync(ctx.TenantId!, ctx.UserId!, id).ConfigureAwait(false);
                if (mission == null) { req.Http.Response.StatusCode = 404; return new ApiErrorResponse { Error = ApiResultEnum.NotFound, Message = "Mission not found" }; }
                mission.DiffSnapshot = null;
                mission.AgentOutput = null;
                mission.PlaybookSnapshots = await _database.Playbooks.GetMissionSnapshotsAsync(id).ConfigureAwait(false);
                return (object)mission;
            },
            api => api
                .WithTag("Missions")
                .WithSummary("Get a mission")
                .WithDescription("Returns a single mission by ID.")
                .WithParameter(OpenApiParameterMetadata.Path("id", "Mission ID (msn_ prefix)"))
                .WithResponse(200, OpenApiJson.For<Mission>("Mission details"))
                .WithResponse(404, OpenApiResponseMetadata.NotFound())
                .WithSecurity("ApiKey"));

            app.Get("/api/v1/missions/{id}/output", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(ctx, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery))
                {
                    return RouteAuthRefusal.Refuse(req, ctx);
                }

                string id = req.Parameters["id"];
                Mission? mission = ctx.IsAdmin
                    ? await _database.Missions.ReadAsync(id).ConfigureAwait(false)
                    : ctx.IsTenantAdmin
                        ? await _database.Missions.ReadAsync(ctx.TenantId!, id).ConfigureAwait(false)
                        : await _database.Missions.ReadAsync(ctx.TenantId!, ctx.UserId!, id).ConfigureAwait(false);
                if (mission == null)
                {
                    req.Http.Response.StatusCode = 404;
                    return new ApiErrorResponse { Error = ApiResultEnum.NotFound, Message = "Mission not found" };
                }

                int offset = Int32.TryParse(QueryValueReader.Read(req, "offset"), out int parsedOffset) ? parsedOffset : 0;
                int length = Int32.TryParse(QueryValueReader.Read(req, "length"), out int parsedLength)
                    ? parsedLength
                    : MissionOutputArtifact.DefaultPageLength;
                try
                {
                    return (object)MissionOutputArtifact.Build(mission, offset, length);
                }
                catch (ArgumentOutOfRangeException ex)
                {
                    req.Http.Response.StatusCode = 400;
                    return new ApiErrorResponse { Error = ApiResultEnum.BadRequest, Message = ex.ParamName + " is outside the valid output page range" };
                }
            },
            api => api
                .WithTag("Missions")
                .WithSummary("Read persisted mission output")
                .WithDescription("Returns one redacted page plus the full safe artifact UTF-8 SHA-256 digest and explicit completeness state.")
                .WithParameter(OpenApiParameterMetadata.Path("id", "Mission ID (msn_ prefix)"))
                .WithParameter(OpenApiParameterMetadata.Query("offset", "Zero-based character offset", false))
                .WithParameter(OpenApiParameterMetadata.Query("length", "Characters to return (maximum 64000)", false))
                .WithResponse(200, OpenApiJson.For<MissionOutputArtifactPage>("Mission output page"))
                .WithResponse(400, OpenApiResponseMetadata.BadRequest())
                .WithResponse(404, OpenApiResponseMetadata.NotFound())
                .WithSecurity("ApiKey"));

            app.Get("/api/v1/missions/{id}/github/pull-request", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(ctx, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery))
                {
                    return RouteAuthRefusal.Refuse(req, ctx);
                }
                string missionId = req.Parameters["id"];
                Mission? mission = ctx.IsAdmin
                    ? await _database.Missions.ReadAsync(missionId).ConfigureAwait(false)
                    : ctx.IsTenantAdmin
                        ? await _database.Missions.ReadAsync(ctx.TenantId!, missionId).ConfigureAwait(false)
                        : await _database.Missions.ReadAsync(ctx.TenantId!, ctx.UserId!, missionId).ConfigureAwait(false);
                if (mission == null)
                {
                    req.Http.Response.StatusCode = 404;
                    return new ApiErrorResponse { Error = ApiResultEnum.NotFound, Message = "Mission not found" };
                }

                try
                {
                    return await _gitHub.GetMissionPullRequestAsync(ctx, mission).ConfigureAwait(false);
                }
                catch (InvalidOperationException ex)
                {
                    req.Http.Response.StatusCode = 400;
                    return new ApiErrorResponse { Error = ApiResultEnum.BadRequest, Message = ex.Message };
                }
            },
            api => api
                .WithTag("Missions")
                .WithSummary("Get GitHub pull-request evidence for a mission")
                .WithDescription("Returns normalized GitHub pull-request review and check evidence for the mission pull request.")
                .WithParameter(OpenApiParameterMetadata.Path("id", "Mission ID (msn_ prefix)"))
                .WithResponse(200, OpenApiJson.For<GitHubPullRequestDetail>("GitHub pull-request evidence"))
                .WithResponse(404, OpenApiResponseMetadata.NotFound())
                .WithSecurity("ApiKey"));

            app.Get("/api/v1/missions/{id}/landing-preview", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(ctx, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery))
                {
                    return RouteAuthRefusal.Refuse(req, ctx);
                }

                string id = req.Parameters["id"];
                Mission? mission = ctx.IsAdmin
                    ? await _database.Missions.ReadAsync(id).ConfigureAwait(false)
                    : ctx.IsTenantAdmin
                        ? await _database.Missions.ReadAsync(ctx.TenantId!, id).ConfigureAwait(false)
                        : await _database.Missions.ReadAsync(ctx.TenantId!, ctx.UserId!, id).ConfigureAwait(false);
                if (mission == null)
                {
                    req.Http.Response.StatusCode = 404;
                    return new ApiErrorResponse { Error = ApiResultEnum.NotFound, Message = "Mission not found" };
                }

                if (String.IsNullOrWhiteSpace(mission.VesselId))
                {
                    req.Http.Response.StatusCode = 400;
                    return new ApiErrorResponse { Error = ApiResultEnum.BadRequest, Message = "Mission does not have an associated vessel" };
                }

                Vessel? vessel = ctx.IsAdmin
                    ? await _database.Vessels.ReadAsync(mission.VesselId).ConfigureAwait(false)
                    : ctx.IsTenantAdmin
                        ? await _database.Vessels.ReadAsync(ctx.TenantId!, mission.VesselId).ConfigureAwait(false)
                        : await _database.Vessels.ReadAsync(ctx.TenantId!, ctx.UserId!, mission.VesselId).ConfigureAwait(false);
                if (vessel == null)
                {
                    req.Http.Response.StatusCode = 404;
                    return new ApiErrorResponse { Error = ApiResultEnum.NotFound, Message = "Mission vessel not found" };
                }

                return await _landingPreview.PreviewForMissionAsync(ctx, vessel, mission).ConfigureAwait(false);
            },
            api => api
                .WithTag("Missions")
                .WithSummary("Preview mission landing readiness")
                .WithDescription("Predicts how Armada would land this mission, including branch policy, check requirements, and likely blockers.")
                .WithParameter(OpenApiParameterMetadata.Path("id", "Mission ID (msn_ prefix)"))
                .WithResponse(200, OpenApiJson.For<LandingPreviewResult>("Mission landing preview"))
                .WithResponse(400, OpenApiResponseMetadata.BadRequest())
                .WithResponse(404, OpenApiResponseMetadata.NotFound())
                .WithSecurity("ApiKey"));

            app.Get("/api/v1/missions/{id}/definition-of-done", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(ctx, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery))
                {
                    return RouteAuthRefusal.Refuse(req, ctx);
                }

                string id = req.Parameters["id"];
                Mission? mission = ctx.IsAdmin
                    ? await _database.Missions.ReadAsync(id).ConfigureAwait(false)
                    : ctx.IsTenantAdmin
                        ? await _database.Missions.ReadAsync(ctx.TenantId!, id).ConfigureAwait(false)
                        : await _database.Missions.ReadAsync(ctx.TenantId!, ctx.UserId!, id).ConfigureAwait(false);
                if (mission == null)
                {
                    req.Http.Response.StatusCode = 404;
                    return new ApiErrorResponse { Error = ApiResultEnum.NotFound, Message = "Mission not found" };
                }

                return await _definitionOfDoneReport.GetForMissionAsync(ctx, mission).ConfigureAwait(false);
            },
            api => api
                .WithTag("Missions")
                .WithSummary("Get mission definition-of-done report")
                .WithDescription("Returns the current definition-of-done configuration for the mission and its latest recorded gate evaluation. Reading it runs no gate and does not change landing readiness.")
                .WithParameter(OpenApiParameterMetadata.Path("id", "Mission ID (msn_ prefix)"))
                .WithResponse(200, OpenApiJson.For<MissionDefinitionOfDoneReport>("Mission definition-of-done report"))
                .WithResponse(404, OpenApiResponseMetadata.NotFound())
                .WithSecurity("ApiKey"));

            app.Get("/api/v1/missions/{id}/auto-land", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(ctx, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery))
                {
                    return RouteAuthRefusal.Refuse(req, ctx);
                }

                string id = req.Parameters["id"];
                Mission? mission = ctx.IsAdmin
                    ? await _database.Missions.ReadAsync(id).ConfigureAwait(false)
                    : ctx.IsTenantAdmin
                        ? await _database.Missions.ReadAsync(ctx.TenantId!, id).ConfigureAwait(false)
                        : await _database.Missions.ReadAsync(ctx.TenantId!, ctx.UserId!, id).ConfigureAwait(false);
                if (mission == null)
                {
                    req.Http.Response.StatusCode = 404;
                    return new ApiErrorResponse { Error = ApiResultEnum.NotFound, Message = "Mission not found" };
                }

                return await _autoLandReport.GetForMissionAsync(ctx, mission).ConfigureAwait(false);
            },
            api => api
                .WithTag("Missions")
                .WithSummary("Get mission auto-land detail")
                .WithDescription("Returns the vessel's current auto-land predicate, the latest recorded auto-land decision for the mission and the latest merge entry with its audit fields, in the caller's scope. Reading it evaluates no predicate and reads no diff.")
                .WithParameter(OpenApiParameterMetadata.Path("id", "Mission ID (msn_ prefix)"))
                .WithResponse(200, OpenApiJson.For<MissionAutoLandReport>("Mission auto-land detail"))
                .WithResponse(404, OpenApiResponseMetadata.NotFound())
                .WithSecurity("ApiKey"));

            app.Get("/api/v1/missions/{id}/recovery", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(ctx, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery))
                {
                    return RouteAuthRefusal.Refuse(req, ctx);
                }

                string id = req.Parameters["id"];
                Mission? mission = ctx.IsAdmin
                    ? await _database.Missions.ReadAsync(id).ConfigureAwait(false)
                    : ctx.IsTenantAdmin
                        ? await _database.Missions.ReadAsync(ctx.TenantId!, id).ConfigureAwait(false)
                        : await _database.Missions.ReadAsync(ctx.TenantId!, ctx.UserId!, id).ConfigureAwait(false);
                if (mission == null)
                {
                    req.Http.Response.StatusCode = 404;
                    return new ApiErrorResponse { Error = ApiResultEnum.NotFound, Message = "Mission not found" };
                }

                return await _recoveryReport.GetForMissionAsync(ctx, mission).ConfigureAwait(false);
            },
            api => api
                .WithTag("Missions")
                .WithSummary("Get mission recovery detail")
                .WithDescription("Returns recorded recovery counters and budgets, rescue missions, linked incidents with runbook executions, and recent recovery events for the mission in the caller's scope. Reading it dispatches nothing and does not infer a landing from mission status.")
                .WithParameter(OpenApiParameterMetadata.Path("id", "Mission ID (msn_ prefix)"))
                .WithResponse(200, OpenApiJson.For<MissionRecoveryReport>("Mission recovery detail"))
                .WithResponse(404, OpenApiResponseMetadata.NotFound())
                .WithSecurity("ApiKey"));

            app.Put<Mission>("/api/v1/missions/{id}", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(ctx, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery))
                {
                    return RouteAuthRefusal.Refuse(req, ctx);
                }
                string id = req.Parameters["id"];
                Mission? existing = ctx.IsAdmin
                    ? await _database.Missions.ReadAsync(id).ConfigureAwait(false)
                    : ctx.IsTenantAdmin
                        ? await _database.Missions.ReadAsync(ctx.TenantId!, id).ConfigureAwait(false)
                        : await _database.Missions.ReadAsync(ctx.TenantId!, ctx.UserId!, id).ConfigureAwait(false);
                if (existing == null) { req.Http.Response.StatusCode = 404; return new ApiErrorResponse { Error = ApiResultEnum.NotFound, Message = "Mission not found" }; }
                Mission incoming = JsonSerializer.Deserialize<Mission>(req.Http.Request.DataAsString, _jsonOptions)
                    ?? throw new InvalidOperationException("Request body could not be deserialized as Mission.");

                // REST, WebSocket and MCP share one metadata update: only the fields the body names change, the
                // vessel and voyage cannot change, and a changed link must name a mission visible to the caller.
                MissionMetadataPatch patch = MissionMetadataPatch.FromBody(incoming, MissionMetadataPatch.ReadFieldNames(req.Http.Request.DataAsString));
                MissionUpdateResult update = await _operations.UpdateMissionMetadataAsync(existing, patch, ctx).ConfigureAwait(false);
                if (!update.Succeeded)
                {
                    req.Http.Response.StatusCode = update.LinkNotFound ? 404 : 409;
                    return new ApiErrorResponse { Error = update.LinkNotFound ? ApiResultEnum.NotFound : ApiResultEnum.Conflict, Message = update.Message };
                }
                return (object)update.Mission;
            },
            api => api
                .WithTag("Missions")
                .WithSummary("Update a mission")
                .WithDescription("Updates the metadata fields the body names (Title, Description, Priority, BranchName, PrUrl, ParentMissionId, DependsOnMissionId, Persona); a field left out keeps its value, and an empty DependsOnMissionId or ParentMissionId clears the link. A changed link must name a mission visible to the caller; otherwise 404 and nothing changes. A different VesselId or VoyageId returns 409.")
                .WithParameter(OpenApiParameterMetadata.Path("id", "Mission ID (msn_ prefix)"))
                .WithRequestBody(OpenApiJson.BodyFor<Mission>("Updated mission data", true))
                .WithResponse(200, OpenApiJson.For<Mission>("Updated mission"))
                .WithResponse(404, OpenApiResponseMetadata.NotFound())
                .WithSecurity("ApiKey"));

            app.Put<StatusTransitionRequest>("/api/v1/missions/{id}/status", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(ctx, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery))
                {
                    return RouteAuthRefusal.Refuse(req, ctx);
                }
                string id = req.Parameters["id"];
                Mission? mission = ctx.IsAdmin
                    ? await _database.Missions.ReadAsync(id).ConfigureAwait(false)
                    : ctx.IsTenantAdmin
                        ? await _database.Missions.ReadAsync(ctx.TenantId!, id).ConfigureAwait(false)
                        : await _database.Missions.ReadAsync(ctx.TenantId!, ctx.UserId!, id).ConfigureAwait(false);
                if (mission == null) { req.Http.Response.StatusCode = 404; return new ApiErrorResponse { Error = ApiResultEnum.NotFound, Message = "Mission not found" }; }

                StatusTransitionRequest transition = JsonSerializer.Deserialize<StatusTransitionRequest>(req.Http.Request.DataAsString, _jsonOptions)
                    ?? throw new InvalidOperationException("Request body could not be deserialized as StatusTransitionRequest.");
                if (String.IsNullOrEmpty(transition.Status))
                    return new ApiErrorResponse { Error = ApiResultEnum.BadRequest, Message = "Status is required" };

                if (!Enum.TryParse<MissionStatusEnum>(transition.Status, true, out MissionStatusEnum newStatus))
                    return new ApiErrorResponse { Error = ApiResultEnum.BadRequest, Message = "Invalid status: " + transition.Status };

                // REST, WebSocket, and MCP share one operator transition path: validation, the
                // manual completion gates, active-dock landing, and intermediate handoff.
                MissionStatusTransitionResult result = await _statusTransitions.TransitionAsync(
                    mission,
                    newStatus,
                    dockId => ctx.IsAdmin
                        ? _database.Docks.ReadAsync(dockId)
                        : ctx.IsTenantAdmin
                            ? _database.Docks.ReadAsync(ctx.TenantId!, dockId)
                            : _database.Docks.ReadAsync(ctx.TenantId!, ctx.UserId!, dockId),
                    captainId => ctx.IsAdmin
                        ? _database.Captains.ReadAsync(captainId)
                        : ctx.IsTenantAdmin
                            ? _database.Captains.ReadAsync(ctx.TenantId!, captainId)
                            : _database.Captains.ReadAsync(ctx.TenantId!, ctx.UserId!, captainId)).ConfigureAwait(false);

                switch (result.Outcome)
                {
                    case MissionStatusTransitionOutcomeEnum.Applied:
                        return (object)result.Mission!;
                    case MissionStatusTransitionOutcomeEnum.Refused:
                        req.Http.Response.StatusCode = 409;
                        return new ApiErrorResponse { Error = ApiResultEnum.Conflict, Message = result.Message };
                    case MissionStatusTransitionOutcomeEnum.MissionMissing:
                        return new ApiErrorResponse { Error = ApiResultEnum.NotFound, Message = result.Message };
                    default:
                        return new ApiErrorResponse { Error = ApiResultEnum.BadRequest, Message = result.Message };
                }
            },
            api => api
                .WithTag("Missions")
                .WithSummary("Transition mission status")
                .WithDescription("Transitions a mission to a new status. Valid transitions: Pending→Assigned, Assigned→InProgress, InProgress→Testing/Review/Complete/Failed, Testing→Review/InProgress/Complete/Failed, Review→Complete/InProgress/Failed. Most states allow →Cancelled.")
                .WithParameter(OpenApiParameterMetadata.Path("id", "Mission ID (msn_ prefix)"))
                .WithRequestBody(OpenApiJson.BodyFor<StatusTransitionRequest>("Target status", true))
                .WithResponse(200, OpenApiJson.For<Mission>("Updated mission"))
                .WithResponse(400, OpenApiResponseMetadata.BadRequest())
                .WithResponse(404, OpenApiResponseMetadata.NotFound())
                .WithSecurity("ApiKey"));

            app.Post<MissionReviewDecisionRequest>("/api/v1/missions/{id}/review/approve", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(ctx, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery))
                {
                    return RouteAuthRefusal.Refuse(req, ctx);
                }

                string id = req.Parameters["id"];
                Mission? existing = ctx.IsAdmin
                    ? await _database.Missions.ReadAsync(id).ConfigureAwait(false)
                    : ctx.IsTenantAdmin
                        ? await _database.Missions.ReadAsync(ctx.TenantId!, id).ConfigureAwait(false)
                        : await _database.Missions.ReadAsync(ctx.TenantId!, ctx.UserId!, id).ConfigureAwait(false);
                if (existing == null) { req.Http.Response.StatusCode = 404; return new ApiErrorResponse { Error = ApiResultEnum.NotFound, Message = "Mission not found" }; }
                MissionReviewDecisionRequest body = JsonSerializer.Deserialize<MissionReviewDecisionRequest>(req.Http.Request.DataAsString, _jsonOptions)
                    ?? new MissionReviewDecisionRequest();

                try
                {
                    Mission mission = await _missionService.ApproveReviewAsync(id, ctx.UserId, body.Comment).ConfigureAwait(false);
                    // A signal belongs to the mission it reports, so the mission's owner sees it.
                    Signal signal = new Signal(SignalTypeEnum.Progress, "Mission " + id + " review approved");
                    signal.TenantId = mission.TenantId;
                    signal.UserId = mission.UserId;
                    await _database.Signals.CreateAsync(signal).ConfigureAwait(false);
                    await _emitEvent("mission.review_approved", "Mission " + id + " review approved",
                        "mission", id, mission.CaptainId, id, mission.VesselId, mission.VoyageId).ConfigureAwait(false);
                    _webSocketHub?.BroadcastMissionChange(mission);
                    return (object)mission;
                }
                catch (InvalidOperationException ioe)
                {
                    req.Http.Response.StatusCode = 400;
                    return new ApiErrorResponse { Error = ApiResultEnum.BadRequest, Message = ioe.Message };
                }
            },
            api => api
                .WithTag("Missions")
                .WithSummary("Approve a mission review gate")
                .WithDescription("Approves a mission waiting at a review gate. Non-terminal stages continue to the next pipeline stage, and terminal stages continue to landing.")
                .WithParameter(OpenApiParameterMetadata.Path("id", "Mission ID (msn_ prefix)"))
                .WithRequestBody(OpenApiJson.BodyFor<MissionReviewDecisionRequest>("Optional review comment", false))
                .WithResponse(200, OpenApiJson.For<Mission>("Updated mission"))
                .WithResponse(400, OpenApiResponseMetadata.BadRequest())
                .WithResponse(404, OpenApiResponseMetadata.NotFound())
                .WithSecurity("ApiKey"));

            app.Post<MissionReviewDecisionRequest>("/api/v1/missions/{id}/review/deny", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(ctx, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery))
                {
                    return RouteAuthRefusal.Refuse(req, ctx);
                }

                string id = req.Parameters["id"];
                Mission? existing = ctx.IsAdmin
                    ? await _database.Missions.ReadAsync(id).ConfigureAwait(false)
                    : ctx.IsTenantAdmin
                        ? await _database.Missions.ReadAsync(ctx.TenantId!, id).ConfigureAwait(false)
                        : await _database.Missions.ReadAsync(ctx.TenantId!, ctx.UserId!, id).ConfigureAwait(false);
                if (existing == null) { req.Http.Response.StatusCode = 404; return new ApiErrorResponse { Error = ApiResultEnum.NotFound, Message = "Mission not found" }; }
                MissionReviewDecisionRequest body = JsonSerializer.Deserialize<MissionReviewDecisionRequest>(req.Http.Request.DataAsString, _jsonOptions)
                    ?? new MissionReviewDecisionRequest();

                try
                {
                    Mission mission = await _missionService.DenyReviewAsync(id, ctx.UserId, body.Comment).ConfigureAwait(false);
                    Signal signal = new Signal(SignalTypeEnum.Progress, "Mission " + id + " review denied");
                    signal.TenantId = mission.TenantId;
                    signal.UserId = mission.UserId;
                    await _database.Signals.CreateAsync(signal).ConfigureAwait(false);
                    await _emitEvent("mission.review_denied", "Mission " + id + " review denied",
                        "mission", id, mission.CaptainId, id, mission.VesselId, mission.VoyageId).ConfigureAwait(false);
                    _webSocketHub?.BroadcastMissionChange(mission);
                    return (object)mission;
                }
                catch (InvalidOperationException ioe)
                {
                    req.Http.Response.StatusCode = 400;
                    return new ApiErrorResponse { Error = ApiResultEnum.BadRequest, Message = ioe.Message };
                }
            },
            api => api
                .WithTag("Missions")
                .WithSummary("Deny a mission review gate")
                .WithDescription("Denies a mission waiting at a review gate. The mission either returns to Pending for rework or fails the pipeline, depending on its review policy.")
                .WithParameter(OpenApiParameterMetadata.Path("id", "Mission ID (msn_ prefix)"))
                .WithRequestBody(OpenApiJson.BodyFor<MissionReviewDecisionRequest>("Optional review comment", false))
                .WithResponse(200, OpenApiJson.For<Mission>("Updated mission"))
                .WithResponse(400, OpenApiResponseMetadata.BadRequest())
                .WithResponse(404, OpenApiResponseMetadata.NotFound())
                .WithSecurity("ApiKey"));

            app.Delete("/api/v1/missions/{id}", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(ctx, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery))
                {
                    return RouteAuthRefusal.Refuse(req, ctx);
                }
                string id = req.Parameters["id"];
                Mission? mission = ctx.IsAdmin
                    ? await _database.Missions.ReadAsync(id).ConfigureAwait(false)
                    : ctx.IsTenantAdmin
                        ? await _database.Missions.ReadAsync(ctx.TenantId!, id).ConfigureAwait(false)
                        : await _database.Missions.ReadAsync(ctx.TenantId!, ctx.UserId!, id).ConfigureAwait(false);
                if (mission == null) { req.Http.Response.StatusCode = 404; return new ApiErrorResponse { Error = ApiResultEnum.NotFound, Message = "Mission not found" }; }

                // REST, WebSocket and MCP share one mission cancel: the finished-mission refusal, the captain
                // recall, the dependent-stage cascade, the event and the broadcast.
                MissionCancellationResult cancellation = await _operations.CancelMissionAsync(mission).ConfigureAwait(false);
                if (!cancellation.Succeeded)
                {
                    req.Http.Response.StatusCode = 409;
                    return new ApiErrorResponse { Error = ApiResultEnum.Conflict, Message = cancellation.Message };
                }
                return (object)cancellation.Mission;
            },
            api => api
                .WithTag("Missions")
                .WithSummary("Cancel a mission")
                .WithDescription("Cancels a mission. A running mission's captain is recalled first, which stops its agent process, and every stage waiting on the mission is cancelled with it. A Complete, Failed or Cancelled mission keeps its outcome: the cancel is refused with 409. Returns the full updated mission.")
                .WithParameter(OpenApiParameterMetadata.Path("id", "Mission ID (msn_ prefix)"))
                .WithResponse(200, OpenApiJson.For<Mission>("Cancelled mission"))
                .WithResponse(404, OpenApiResponseMetadata.NotFound())
                .WithResponse(409, OpenApiJson.For<ApiErrorResponse>("The mission is Complete, Failed or Cancelled, or its captain could not be recalled"))
                .WithSecurity("ApiKey"));

            app.Delete("/api/v1/missions/{id}/purge", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(ctx, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery))
                {
                    return RouteAuthRefusal.Refuse(req, ctx);
                }
                string id = req.Parameters["id"];
                Mission? mission = ctx.IsAdmin
                    ? await _database.Missions.ReadAsync(id).ConfigureAwait(false)
                    : ctx.IsTenantAdmin
                        ? await _database.Missions.ReadAsync(ctx.TenantId!, id).ConfigureAwait(false)
                        : await _database.Missions.ReadAsync(ctx.TenantId!, ctx.UserId!, id).ConfigureAwait(false);
                if (mission == null) { req.Http.Response.StatusCode = 404; return new ApiErrorResponse { Error = ApiResultEnum.NotFound, Message = "Mission not found" }; }

                // REST, WebSocket and MCP share one mission purge: the at-work refusal, the guarded dock and
                // worktree removal, the log and diff deletion, and the event.
                WorkPurgeResult purge = await _operations.PurgeMissionAsync(mission).ConfigureAwait(false);
                if (!purge.Succeeded)
                {
                    req.Http.Response.StatusCode = 409;
                    return new ApiErrorResponse { Error = ApiResultEnum.Conflict, Message = purge.Message };
                }

                return (object)new { Status = "deleted", MissionId = id };
            },
            api => api
                .WithTag("Missions")
                .WithSummary("Permanently delete a mission")
                .WithDescription("Permanently deletes a mission with its dock record, worktree, log files and saved diff. A mission a captain is working is refused with 409. This cannot be undone.")
                .WithParameter(OpenApiParameterMetadata.Path("id", "Mission ID (msn_ prefix)"))
                .WithResponse(200, OpenApiJson.For<object>("Deleted mission"))
                .WithResponse(404, OpenApiResponseMetadata.NotFound())
                .WithResponse(409, OpenApiJson.For<ApiErrorResponse>("A captain is working the mission"))
                .WithSecurity("ApiKey"));

            app.Post<DeleteMultipleRequest>("/api/v1/missions/delete/multiple", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(ctx, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery))
                {
                    return RouteAuthRefusal.Refuse(req, ctx);
                }
                DeleteMultipleRequest? body = JsonSerializer.Deserialize<DeleteMultipleRequest>(req.Http.Request.DataAsString, _jsonOptions);
                if (body == null || body.Ids == null || body.Ids.Count == 0)
                    return (object)new ApiErrorResponse { Error = ApiResultEnum.BadRequest, Message = "Ids is required and must not be empty" };

                DeleteMultipleResult result = await _operations.PurgeMissionsAsync(
                    body.Ids,
                    missionId => ctx.IsAdmin
                        ? _database.Missions.ReadAsync(missionId)
                        : ctx.IsTenantAdmin
                            ? _database.Missions.ReadAsync(ctx.TenantId!, missionId)
                            : _database.Missions.ReadAsync(ctx.TenantId!, ctx.UserId!, missionId)).ConfigureAwait(false);
                return (object)result;
            },
            api => api
                .WithTag("Missions")
                .WithSummary("Batch delete multiple missions")
                .WithDescription("Permanently deletes multiple missions by ID, each by the single-mission purge rule: a mission a captain is working is skipped with its reason. Returns a summary of deleted and skipped entries. This cannot be undone.")
                .WithRequestBody(OpenApiJson.BodyFor<DeleteMultipleRequest>("List of mission IDs to delete"))
                .WithResponse(200, OpenApiJson.For<DeleteMultipleResult>("Delete result summary"))
                .WithSecurity("ApiKey"));

            app.Post<MissionRestartRequest>("/api/v1/missions/{id}/restart", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(ctx, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery))
                {
                    return RouteAuthRefusal.Refuse(req, ctx);
                }
                string id = req.Parameters["id"];
                Mission? mission = ctx.IsAdmin
                    ? await _database.Missions.ReadAsync(id).ConfigureAwait(false)
                    : ctx.IsTenantAdmin
                        ? await _database.Missions.ReadAsync(ctx.TenantId!, id).ConfigureAwait(false)
                        : await _database.Missions.ReadAsync(ctx.TenantId!, ctx.UserId!, id).ConfigureAwait(false);
                if (mission == null) { req.Http.Response.StatusCode = 404; return new ApiErrorResponse { Error = ApiResultEnum.NotFound, Message = "Mission not found" }; }

                string? title = null;
                string? description = null;
                if (!String.IsNullOrWhiteSpace(req.Http.Request.DataAsString))
                {
                    try
                    {
                        MissionRestartRequest? body = JsonSerializer.Deserialize<MissionRestartRequest>(req.Http.Request.DataAsString, _jsonOptions);
                        title = body?.Title;
                        description = body?.Description;
                    }
                    catch (JsonException)
                    {
                        req.Http.Response.StatusCode = 400;
                        return new ApiErrorResponse { Error = ApiResultEnum.BadRequest, Message = "Request body could not be read as a restart request." };
                    }
                }

                // REST, WebSocket and MCP share one restart: the eligibility rule (LandingFailed is refused with a
                // pointer to retry-landing), the capacity gate, the owned signal, the event and the broadcast.
                MissionRestartResult restart;
                try
                {
                    restart = await _operations.RestartMissionAsync(mission, title, description).ConfigureAwait(false);
                }
                catch (FleetCapacityAdmissionException capacity)
                {
                    req.Http.Response.StatusCode = 409;
                    return new
                    {
                        Error = capacity.Message,
                        capacity.Code,
                        capacity.ActiveCount,
                        capacity.Limit,
                        capacity.CandidateVesselId,
                        capacity.LaneMembers
                    };
                }

                if (!restart.Succeeded)
                {
                    req.Http.Response.StatusCode = 409;
                    return new ApiErrorResponse { Error = ApiResultEnum.Conflict, Message = restart.Message };
                }

                return (object)restart.Mission;
            },
            api => api
                .WithTag("Missions")
                .WithSummary("Restart a failed or cancelled mission")
                .WithDescription("Resets a Failed or Cancelled mission back to Pending so it can be re-dispatched. Optionally update the title and description (instructions) before restarting. Clears captain assignment, branch, PR URL, and timing fields. Any other status is refused with 409; a LandingFailed mission keeps its produced work, and the refusal names retry-landing.")
                .WithParameter(OpenApiParameterMetadata.Path("id", "Mission ID (msn_ prefix)"))
                .WithRequestBody(OpenApiJson.BodyFor<MissionRestartRequest>("Optional updated instructions", false))
                .WithResponse(200, OpenApiJson.For<Mission>("Restarted mission"))
                .WithResponse(400, OpenApiResponseMetadata.BadRequest())
                .WithResponse(404, OpenApiResponseMetadata.NotFound())
                .WithResponse(409, OpenApiJson.For<ApiErrorResponse>("The mission is not Failed or Cancelled, or the fleet has no capacity"))
                .WithSecurity("ApiKey"));

            app.Post("/api/v1/missions/{id}/retry-landing", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(ctx, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery))
                {
                    return RouteAuthRefusal.Refuse(req, ctx);
                }
                string id = req.Parameters["id"];
                Mission? mission = ctx.IsAdmin
                    ? await _database.Missions.ReadAsync(id).ConfigureAwait(false)
                    : await _database.Missions.ReadAsync(ctx.TenantId!, id).ConfigureAwait(false);
                if (mission == null) { req.Http.Response.StatusCode = 404; return new ApiErrorResponse { Error = ApiResultEnum.NotFound, Message = "Mission not found" }; }

                if (mission.Status != MissionStatusEnum.WorkProduced && mission.Status != MissionStatusEnum.LandingFailed)
                {
                    req.Http.Response.StatusCode = 400;
                    return new ApiErrorResponse { Error = ApiResultEnum.BadRequest, Message = "Only WorkProduced or LandingFailed missions can retry landing (current: " + mission.Status + ")" };
                }

                bool success = await _landingService.RetryLandingAsync(id, ctx.TenantId).ConfigureAwait(false);
                mission = await _database.Missions.ReadAsync(id).ConfigureAwait(false);
                mission = await _database.Missions.ReadAsync(id).ConfigureAwait(false);
                if (!success)
                {
                    string reason = "Landing failed.";
                    if (mission != null)
                    {
                        if (String.IsNullOrEmpty(mission.BranchName)) reason = "Mission has no branch name -- the branch may have been cleaned up.";
                        else if (String.IsNullOrEmpty(mission.VesselId)) reason = "Mission has no vessel assigned.";
                        else if (mission.Status == MissionStatusEnum.LandingFailed) reason = "Rebase or merge failed -- the branch may have conflicts with the target branch.";
                        else if (mission.Status == MissionStatusEnum.WorkProduced) reason = "Landing handler failed -- check server logs for details.";
                    }
                    req.Http.Response.StatusCode = 409;
                    return new ApiErrorResponse { Error = ApiResultEnum.Conflict, Message = reason };
                }
                return (object)new { Status = "landed", MissionId = id, MissionStatus = mission?.Status.ToString() };
            },
            api => api
                .WithTag("Missions")
                .WithSummary("Retry landing for a mission")
                .WithDescription("Rebases the mission branch onto the current target and re-attempts landing. Only available for WorkProduced or LandingFailed missions.")
                .WithParameter(OpenApiParameterMetadata.Path("id", "Mission ID (msn_ prefix)"))
                .WithResponse(200, OpenApiJson.For<object>("Landing result"))
                .WithResponse(400, OpenApiResponseMetadata.BadRequest())
                .WithResponse(404, OpenApiResponseMetadata.NotFound())
                .WithSecurity("ApiKey"));

            app.Get("/api/v1/missions/{id}/diff", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(ctx, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery))
                {
                    return RouteAuthRefusal.Refuse(req, ctx);
                }
                string id = req.Parameters["id"];
                Mission? mission = ctx.IsAdmin
                    ? await _database.Missions.ReadAsync(id).ConfigureAwait(false)
                    : ctx.IsTenantAdmin
                        ? await _database.Missions.ReadAsync(ctx.TenantId!, id).ConfigureAwait(false)
                        : await _database.Missions.ReadAsync(ctx.TenantId!, ctx.UserId!, id).ConfigureAwait(false);
                if (mission == null) { req.Http.Response.StatusCode = 404; return new ApiErrorResponse { Error = ApiResultEnum.NotFound, Message = "Mission not found" }; }

                // REST, WebSocket and MCP read a diff through one reader: the saved diff file, then the stored snapshot,
                // then the live worktree, reading docks and captains in the caller's scope.
                MissionDiffResult diff = await MissionDiffReader.ReadAsync(
                    _database,
                    _settings.LogDirectory,
                    _git,
                    mission,
                    dockId => ctx.IsAdmin
                        ? _database.Docks.ReadAsync(dockId)
                        : ctx.IsTenantAdmin
                            ? _database.Docks.ReadAsync(ctx.TenantId!, dockId)
                            : _database.Docks.ReadAsync(ctx.TenantId!, ctx.UserId!, dockId),
                    captainId => ctx.IsAdmin
                        ? _database.Captains.ReadAsync(captainId)
                        : ctx.IsTenantAdmin
                            ? _database.Captains.ReadAsync(ctx.TenantId!, captainId)
                            : _database.Captains.ReadAsync(ctx.TenantId!, ctx.UserId!, captainId),
                    vesselId => ctx.IsAdmin
                        ? _database.Docks.EnumerateByVesselAsync(vesselId)
                        : _database.Docks.EnumerateByVesselAsync(ctx.TenantId!, vesselId)).ConfigureAwait(false);
                if (!diff.Available)
                {
                    req.Http.Response.StatusCode = 404;
                    return new ApiErrorResponse { Error = ApiResultEnum.NotFound, Message = diff.Message };
                }
                return (object)new { MissionId = id, Branch = diff.Branch, Diff = diff.Diff };
            },
            api => api
                .WithTag("Missions")
                .WithSummary("Get diff for a mission")
                .WithDescription("Returns the git diff of changes made by a captain in the mission's worktree.")
                .WithParameter(OpenApiParameterMetadata.Path("id", "Mission ID (msn_ prefix)"))
                .WithResponse(404, OpenApiResponseMetadata.NotFound())
                .WithSecurity("ApiKey"));

            app.Get("/api/v1/missions/{id}/log", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(ctx, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery))
                {
                    return RouteAuthRefusal.Refuse(req, ctx);
                }
                string id = req.Parameters["id"];
                Mission? mission = ctx.IsAdmin
                    ? await _database.Missions.ReadAsync(id).ConfigureAwait(false)
                    : ctx.IsTenantAdmin
                        ? await _database.Missions.ReadAsync(ctx.TenantId!, id).ConfigureAwait(false)
                        : await _database.Missions.ReadAsync(ctx.TenantId!, ctx.UserId!, id).ConfigureAwait(false);
                if (mission == null) { req.Http.Response.StatusCode = 404; return new ApiErrorResponse { Error = ApiResultEnum.NotFound, Message = "Mission not found" }; }

                bool formatted = String.Equals(QueryValueReader.Read(req, "formatted"), "true", StringComparison.OrdinalIgnoreCase);
                int? offset = null;
                int? lineCount = null;
                string? offsetParam = QueryValueReader.Read(req, "offset");
                if (!String.IsNullOrEmpty(offsetParam) && Int32.TryParse(offsetParam, out int parsedOffset)) offset = parsedOffset;
                string? linesParam = QueryValueReader.Read(req, "lines");
                if (!String.IsNullOrEmpty(linesParam) && Int32.TryParse(linesParam, out int parsedLines)) lineCount = parsedLines;
                return await SessionLogReader.ReadMissionLogAsync(_settings.LogDirectory, mission.Id, offset, lineCount, 200, formatted).ConfigureAwait(false);
            },
            api => api
                .WithTag("Missions")
                .WithSummary("Get log for a mission")
                .WithDescription("Returns the session log for a mission. Supports pagination via ?lines=N (default 200) and ?offset=N query parameters.")
                .WithParameter(OpenApiParameterMetadata.Path("id", "Mission ID (msn_ prefix)"))
                .WithResponse(404, OpenApiResponseMetadata.NotFound())
                .WithSecurity("ApiKey"));

            app.Get("/api/v1/missions/{id}/instructions", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(ctx, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery))
                {
                    return RouteAuthRefusal.Refuse(req, ctx);
                }
                string id = req.Parameters["id"];
                Mission? mission = ctx.IsAdmin
                    ? await _database.Missions.ReadAsync(id).ConfigureAwait(false)
                    : ctx.IsTenantAdmin
                        ? await _database.Missions.ReadAsync(ctx.TenantId!, id).ConfigureAwait(false)
                        : await _database.Missions.ReadAsync(ctx.TenantId!, ctx.UserId!, id).ConfigureAwait(false);
                if (mission == null) { req.Http.Response.StatusCode = 404; return new ApiErrorResponse { Error = ApiResultEnum.NotFound, Message = "Mission not found" }; }

                MissionInstructionsPath? resolved = await ResolveMissionInstructionsPathAsync(ctx, mission).ConfigureAwait(false);
                if (resolved == null)
                {
                    string instructionsDir = Path.Combine(_settings.LogDirectory, "instructions");
                    string[] candidates = Directory.Exists(instructionsDir)
                        ? Directory.GetFiles(instructionsDir, id + ".*")
                        : Array.Empty<string>();

                    if (candidates.Length > 0)
                    {
                        string snapshotPath = candidates[0];
                        string snapshotFileName = Path.GetFileName(snapshotPath);
                        try
                        {
                            string snapshotContent = await ReadFileSharedAsync(snapshotPath).ConfigureAwait(false);
                            return (object)new { MissionId = id, FileName = snapshotFileName, Content = snapshotContent };
                        }
                        catch (IOException)
                        {
                            return (object)new { MissionId = id, FileName = snapshotFileName, Content = "" };
                        }
                    }

                    req.Http.Response.StatusCode = 404;
                    return new ApiErrorResponse { Error = ApiResultEnum.NotFound, Message = "Mission instructions are unavailable because neither a live dock/worktree nor a saved instructions snapshot could be found" };
                }

                try
                {
                    string content = File.Exists(resolved.Path)
                        ? await ReadFileSharedAsync(resolved.Path).ConfigureAwait(false)
                        : "";
                    return (object)new { MissionId = id, FileName = resolved.FileName, Content = content };
                }
                catch (IOException)
                {
                    return (object)new { MissionId = id, FileName = resolved.FileName, Content = "" };
                }
            },
            api => api
                .WithTag("Missions")
                .WithSummary("Get mission instructions")
                .WithDescription("Returns the runtime-specific instruction file generated for a mission, such as CLAUDE.md, CODEX.md, CURSOR.md, AGENTS.md, GEMINI.md, or MUX.md.")
                .WithParameter(OpenApiParameterMetadata.Path("id", "Mission ID (msn_ prefix)"))
                .WithResponse(404, OpenApiResponseMetadata.NotFound())
                .WithSecurity("ApiKey"));
        }

        private static MissionHistorySummaryResult BuildMissionHistorySummary(MissionHistoryQuery query, IEnumerable<MissionHistoryPoint> points)
        {
            DateTime fromUtc = query.FromUtc.Kind == DateTimeKind.Utc ? query.FromUtc : query.FromUtc.ToUniversalTime();
            DateTime toUtc = query.ToUtc.Kind == DateTimeKind.Utc ? query.ToUtc : query.ToUtc.ToUniversalTime();
            int bucketMinutes = query.BucketMinutes > 0 ? query.BucketMinutes : 60;
            TimeSpan bucketSize = TimeSpan.FromMinutes(bucketMinutes);
            long bucketTicks = Math.Max(bucketSize.Ticks, TimeSpan.FromMinutes(1).Ticks);

            MissionHistorySummaryResult result = new MissionHistorySummaryResult
            {
                FromUtc = fromUtc,
                ToUtc = toUtc,
                BucketMinutes = bucketMinutes
            };

            SortedDictionary<long, MissionHistoryBucket> buckets = new SortedDictionary<long, MissionHistoryBucket>();
            long startTicks = (fromUtc.Ticks / bucketTicks) * bucketTicks;
            for (long ticks = startTicks; ticks < toUtc.Ticks; ticks += bucketTicks)
            {
                buckets[ticks] = new MissionHistoryBucket { StartUtc = new DateTime(ticks, DateTimeKind.Utc) };
            }

            foreach (MissionHistoryPoint point in points)
            {
                DateTime createdUtc = point.CreatedUtc.Kind == DateTimeKind.Utc ? point.CreatedUtc : point.CreatedUtc.ToUniversalTime();
                if (createdUtc < fromUtc || createdUtc >= toUtc) continue;

                long bucketStartTicks = (createdUtc.Ticks / bucketTicks) * bucketTicks;
                if (!buckets.TryGetValue(bucketStartTicks, out MissionHistoryBucket? bucket))
                {
                    bucket = new MissionHistoryBucket { StartUtc = new DateTime(bucketStartTicks, DateTimeKind.Utc) };
                    buckets[bucketStartTicks] = bucket;
                }

                // Complete covers every state in which the captain has produced work or gone further,
                // not only landed missions, so produced and reviewed work still reads as done when
                // landing is off. Failed and LandingFailed are failures; only in-flight and cancelled
                // missions are Other.
                if (point.Status == MissionStatusEnum.WorkProduced
                    || point.Status == MissionStatusEnum.PullRequestOpen
                    || point.Status == MissionStatusEnum.Testing
                    || point.Status == MissionStatusEnum.Review
                    || point.Status == MissionStatusEnum.Complete)
                {
                    bucket.CompleteCount++;
                    result.CompleteCount++;
                }
                else if (point.Status == MissionStatusEnum.Failed || point.Status == MissionStatusEnum.LandingFailed)
                {
                    bucket.FailedCount++;
                    result.FailedCount++;
                }
                else
                {
                    bucket.OtherCount++;
                    result.OtherCount++;
                }
            }

            result.TotalCount = result.CompleteCount + result.FailedCount + result.OtherCount;
            result.Buckets = buckets.Values.ToList();
            return result;
        }
    }
}
