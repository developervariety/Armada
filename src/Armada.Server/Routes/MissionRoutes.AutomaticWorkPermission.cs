namespace Armada.Server.Routes
{
    using System;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using System.Threading.Tasks;
    using Armada.Core.Authorization;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;
    using Armada.Server;
    using WatsonWebserver;
    using WatsonWebserver.Core;
    using WatsonWebserver.Core.OpenApi;

    public partial class MissionRoutes
    {
        // This endpoint carries exact launch/grant identities. The general response formatter uses
        // microsecond dates, so materialize precise JSON before that formatter can truncate a tick.
        private static readonly JsonSerializerOptions _AutomaticPermissionWireOptions = new JsonSerializerOptions
        {
            Converters = { new JsonStringEnumConverter() }
        };

        private static object AutomaticPermissionWire(object value) =>
            JsonSerializer.SerializeToElement(value, _AutomaticPermissionWireOptions);

        private void RegisterAutomaticWorkPermissionRoutes(Webserver app,
            Func<HttpContextBase, Task<AuthContext>> authenticate, IAuthorizationService authz)
        {
            app.Get("/api/v1/missions/{id}/action-permissions", async (ApiRequest req) =>
            {
                AuthContext caller = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(caller, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery))
                    return RouteAuthRefusal.Refuse(req, caller);
                if (!String.IsNullOrWhiteSpace(caller.MissionId))
                    return RouteAuthRefusal.Forbid(req, "Automatic-work permissions are an operator control.");
                Mission? mission = await CallerScopedRead.ReadMissionAsync(_database, caller, req.Parameters["id"]).ConfigureAwait(false);
                if (mission == null)
                {
                    req.Http.Response.StatusCode = 404;
                    return new ApiErrorResponse { Error = ApiResultEnum.NotFound, Message = "Mission not found" };
                }
                return AutomaticPermissionWire(new
                {
                    mission.Id, mission.StartedUtc, mission.CommitHash,
                    mission.AutomaticWorkPermission, mission.AutomaticWorkPermissionInvalid
                });
            }, api => api
                .WithTag("Missions")
                .WithSummary("Read a mission's automatic-work permission state")
                .WithDescription("Operator-only, caller-scoped current launch, source and optional independent action grants. An invalid stored permission holds work.")
                .WithParameter(OpenApiParameterMetadata.Path("id", "Mission ID"))
                .WithResponse(404, OpenApiResponseMetadata.NotFound())
                .WithSecurity("ApiKey"));

            app.Post<MissionAutomaticWorkPermissionRequest>("/api/v1/missions/{id}/action-permissions", async (ApiRequest req) =>
            {
                AuthContext caller = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(caller, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery))
                    return RouteAuthRefusal.Refuse(req, caller);
                if (!String.IsNullOrWhiteSpace(caller.MissionId))
                    return RouteAuthRefusal.Forbid(req, "Automatic-work permissions are an operator control.");
                try
                {
                    MissionAutomaticWorkPermissionRequest? body = JsonSerializer.Deserialize<MissionAutomaticWorkPermissionRequest>(
                        req.Http.Request.DataAsString, _jsonOptions);
                    if (body == null) throw new ArgumentException("A permission request body is required.");
                    Mission? mission = await _missionService.UpdateAutomaticWorkPermissionAsync(
                        caller, req.Parameters["id"], body).ConfigureAwait(false);
                    if (mission == null)
                    {
                        req.Http.Response.StatusCode = 404;
                        return new ApiErrorResponse { Error = ApiResultEnum.NotFound, Message = "Mission not found" };
                    }
                    _webSocketHub?.BroadcastMissionChange(mission);
                    return AutomaticPermissionWire(mission);
                }
                catch (JsonException)
                {
                    req.Http.Response.StatusCode = 400;
                    return new ApiErrorResponse { Error = ApiResultEnum.BadRequest, Message = "Permission request could not be read." };
                }
                catch (ArgumentException ex)
                {
                    req.Http.Response.StatusCode = 400;
                    return new ApiErrorResponse { Error = ApiResultEnum.BadRequest, Message = ex.Message };
                }
                catch (UnauthorizedAccessException)
                {
                    return RouteAuthRefusal.Forbid(req, "Only an authenticated operator can change automatic-work permissions.");
                }
                catch (InvalidOperationException ex)
                {
                    req.Http.Response.StatusCode = 409;
                    return new ApiErrorResponse { Error = ApiResultEnum.Conflict, Message = ex.Message };
                }
            }, api => api
                .WithTag("Missions")
                .WithSummary("Enable, grant or revoke one mission automatic action")
                .WithDescription("Authenticated operator control. Enable holds both actions; grants bind the current launch and source, expire within one hour, and never approve Review. Writes and their audit are atomic. No restart or work begins from this request.")
                .WithParameter(OpenApiParameterMetadata.Path("id", "Mission ID"))
                .WithRequestBody(OpenApiJson.BodyFor<MissionAutomaticWorkPermissionRequest>("Explicit operation and reason"))
                .WithResponse(200, OpenApiJson.For<Mission>("Updated mission"))
                .WithResponse(400, OpenApiResponseMetadata.BadRequest())
                .WithResponse(404, OpenApiResponseMetadata.NotFound())
                .WithSecurity("ApiKey"));
        }
    }
}
