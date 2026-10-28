namespace Armada.Server.Routes
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using WatsonWebserver;
    using WatsonWebserver.Core;
    using WatsonWebserver.Core.OpenApi;

    public partial class VoyageRoutes
    {
        private void RegisterResearchReplay(Webserver app, Func<HttpContextBase, Task<AuthContext>> authenticate,
            IAuthorizationService authz)
        {
            ResearchReplayService service = new ResearchReplayService(_database, _admiral,
                _settings ?? new ArmadaSettings(), new GitService(_logging, database: _database),
                new OperationNotifier(_emitEvent, mission => _webSocketHub?.BroadcastMissionChange(mission), voyage => _webSocketHub?.BroadcastVoyageChange(voyage)));
            app.Post("/api/v1/voyages/{id}/research-replay", async req =>
            {
                AuthContext caller = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(caller, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery)
                    || !caller.IsAdmin || !String.IsNullOrEmpty(caller.MissionId)
                    || !String.IsNullOrEmpty(caller.AskThreadId) || !String.IsNullOrEmpty(caller.AskTurnId))
                    return RouteAuthRefusal.Refuse(req, caller);
                try
                {
                    ResearchReplayRequest request = JsonSerializer.Deserialize<ResearchReplayRequest>(req.Http.Request.DataAsString,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new ArgumentException("Body required.");
                    ResearchReplayPlan plan = await service.ExecuteAsync(caller, req.Parameters["id"], request).ConfigureAwait(false);
                    return (object)plan;
                }
                catch (UnauthorizedAccessException) { return ReplayError(req, 403, ApiResultEnum.Forbidden, "Current exact-owner global operator authority is required."); }
                catch (KeyNotFoundException) { return ReplayError(req, 404, ApiResultEnum.NotFound, "Research voyage not found."); }
                catch (Exception ex) when (ex is ArgumentException || ex is JsonException)
                { return ReplayError(req, 400, ApiResultEnum.BadRequest, "The Research replay request is invalid."); }
                catch (InvalidOperationException)
                { return ReplayError(req, 409, ApiResultEnum.Conflict, "The Research chain is not ready or the preview is stale; no partial replay was applied."); }
            }, api => api.WithTag("Voyages").WithSummary("Preview or atomically replay all six Research duties while preserving prior attempts")
                .WithRequestBody(OpenApiJson.BodyFor<ResearchReplayRequest>("Exact source, dry-run preview or idempotent expected-state apply", true))
                .WithResponse(200, OpenApiJson.For<ResearchReplayPlan>("Safe full-chain plan or durable replay receipt")).WithSecurity("ApiKey"));

            app.Get("/api/v1/research-replays/{id}/missions/{missionId}/output", async req =>
            {
                AuthContext caller = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(caller, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery))
                    return RouteAuthRefusal.Refuse(req, caller);
                try
                {
                    int offset = Int32.TryParse(QueryValueReader.Read(req, "offset"), out int parsedOffset) ? parsedOffset : 0;
                    int length = Int32.TryParse(QueryValueReader.Read(req, "length"), out int parsedLength) ? parsedLength : MissionOutputArtifact.DefaultPageLength;
                    MissionOutputArtifactPage? page = await service.ReadOutputAsync(caller, req.Parameters["id"], req.Parameters["missionId"], offset, length).ConfigureAwait(false);
                    return page == null ? ReplayError(req, 404, ApiResultEnum.NotFound, "Prior attempt not found.") : (object)page;
                }
                catch (UnauthorizedAccessException) { return ReplayError(req, 403, ApiResultEnum.Forbidden, "Current exact-owner global operator authority is required."); }
                catch (ArgumentException) { return ReplayError(req, 400, ApiResultEnum.BadRequest, "Invalid output page."); }
            }, api => api.WithTag("Voyages").WithSummary("Read an immutable prior Research report with redaction and digest-backed paging")
                .WithResponse(200, OpenApiJson.For<MissionOutputArtifactPage>("Prior attempt output page")).WithSecurity("ApiKey"));
        }

        private static ApiErrorResponse ReplayError(ApiRequest request, int status, ApiResultEnum code, string message)
        { request.Http.Response.StatusCode = status; return new ApiErrorResponse { Error = code, Message = message }; }
    }
}
