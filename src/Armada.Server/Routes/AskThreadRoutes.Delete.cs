namespace Armada.Server.Routes
{
    using System;
    using System.Text.Json;
    using System.Threading.Tasks;
    using WatsonWebserver;
    using WatsonWebserver.Core;
    using WatsonWebserver.Core.OpenApi;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;

    public sealed partial class AskThreadRoutes
    {
        private void RegisterDeleteRoutes(Webserver app, Func<HttpContextBase, Task<AuthContext>> authenticate, IAuthorizationService authz)
        {
            app.Delete("/api/v1/ask/threads/{id}", async (ApiRequest req) =>
            {
                AuthContext caller = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(caller, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery)) return RouteAuthRefusal.Refuse(req, caller);
                try
                {
                    AskThreadDeleteResult deletion = await _Threads.DeleteThreadAsync(caller, req.Parameters["id"]).ConfigureAwait(false);
                    if (deletion.Outcome == AskThreadDeleteOutcomeEnum.NotFound) return Error(req, 404, ApiResultEnum.NotFound, deletion.Message);
                    if (deletion.Outcome != AskThreadDeleteOutcomeEnum.Deleted) return Error(req, 409, ApiResultEnum.Conflict, deletion.Message);
                    req.Http.Response.StatusCode = 204;
                    return null;
                }
                catch (Exception ex) when (TryMapException(req, ex, out ApiErrorResponse error)) { return error; }
            }, api => api.WithTag("Ask Threads").WithSummary("Delete an archived saved Ask thread")
                .WithDescription("Permanently deletes an archived conversation the caller owns, with its turns and proposals. Refused with 409 while the conversation has an active or uncertain turn or a proposal waiting for a decision. This cannot be undone.")
                .WithParameter(OpenApiParameterMetadata.Path("id", "Ask thread ID"))
                .WithResponse(204, OpenApiResponseMetadata.NoContent())
                .WithResponse(404, OpenApiResponseMetadata.NotFound())
                .WithResponse(409, OpenApiJson.For<ApiErrorResponse>("Conversation cannot be deleted"))
                .WithSecurity("ApiKey"));

            app.Post<DeleteMultipleRequest>("/api/v1/ask/threads/delete/multiple", async (ApiRequest req) =>
            {
                AuthContext caller = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(caller, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery)) return RouteAuthRefusal.Refuse(req, caller);
                try
                {
                    DeleteMultipleRequest body = Deserialize<DeleteMultipleRequest>(req);
                    if (body.Ids == null || body.Ids.Count == 0) return Error(req, 400, ApiResultEnum.BadRequest, "Ids is required and must not be empty");
                    return await _Threads.DeleteThreadsAsync(caller, body.Ids).ConfigureAwait(false);
                }
                catch (Exception ex) when (TryMapException(req, ex, out ApiErrorResponse error)) { return error; }
            }, api => api.WithTag("Ask Threads").WithSummary("Batch delete archived saved Ask threads")
                .WithDescription("Deletes up to 100 archived conversations the caller owns by the same rule as a single delete. A conversation that cannot be deleted is reported in skipped with its reason. This cannot be undone.")
                .WithRequestBody(OpenApiJson.BodyFor<DeleteMultipleRequest>("List of Ask thread IDs to delete", true))
                .WithResponse(200, OpenApiJson.For<DeleteMultipleResult>("Delete result summary"))
                .WithSecurity("ApiKey"));
        }
    }
}
