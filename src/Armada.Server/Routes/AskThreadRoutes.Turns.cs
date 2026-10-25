namespace Armada.Server.Routes
{
    using System;
    using System.Collections.Generic;
    using System.Threading.Tasks;
    using WatsonWebserver;
    using WatsonWebserver.Core;
    using WatsonWebserver.Core.OpenApi;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;
    using Armada.Server;

    public sealed partial class AskThreadRoutes
    {
        private void RegisterTurnRoutes(Webserver app, Func<HttpContextBase, Task<AuthContext>> authenticate, IAuthorizationService authz)
        {
            app.Get("/api/v1/ask/threads/{id}/turns", async req =>
            {
                AuthContext caller = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(caller, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery)) return RouteAuthRefusal.Refuse(req, caller);
                try
                {
                    long? before = Int64.TryParse(req.Query.GetValueOrDefault("beforeSequence"), out long parsed) ? parsed : null;
                    int size = Int32.TryParse(req.Query.GetValueOrDefault("pageSize"), out int parsedSize) ? parsedSize : 100;
                    List<AskTurn>? turns = await _Threads.EnumerateTurnsAsync(caller, req.Parameters["id"], before, size).ConfigureAwait(false);
                    if (turns == null) return Error(req, 404, ApiResultEnum.NotFound, "Ask thread not found.");
                    return turns;
                }
                catch (Exception ex) when (TryMapException(req, ex, out ApiErrorResponse error)) { return error; }
            }, api => api.WithTag("Ask Threads").WithSummary("Read saved conversation turns")
                .WithResponse(200, OpenApiJson.For<AskTurn[]>("Turns, oldest first within the page"))
                .WithSecurity("ApiKey"));

            app.Get("/api/v1/ask/threads/{id}/turns/{turnId}", async req =>
            {
                AuthContext caller = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(caller, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery)) return RouteAuthRefusal.Refuse(req, caller);
                try
                {
                    AskTurn? turn = await _Threads.GetTurnAsync(caller, req.Parameters["id"], req.Parameters["turnId"]).ConfigureAwait(false);
                    if (turn == null) return Error(req, 404, ApiResultEnum.NotFound, "Ask turn not found.");
                    return turn;
                }
                catch (Exception ex) when (TryMapException(req, ex, out ApiErrorResponse error)) { return error; }
            }, api => api.WithTag("Ask Threads").WithSummary("Read one saved turn")
                .WithResponse(200, OpenApiJson.For<AskTurn>("Stored turn"))
                .WithResponse(404, OpenApiResponseMetadata.NotFound())
                .WithSecurity("ApiKey"));

            app.Post<AskTurnSendRequest>("/api/v1/ask/threads/{id}/turns", async req =>
            {
                AuthContext caller = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(caller, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery)) return RouteAuthRefusal.Refuse(req, caller);
                try
                {
                    AskTurnSendRequest body = Deserialize<AskTurnSendRequest>(req);
                    AskTurnStart result = await _Threads.SendTurnAsync(caller, req.Parameters["id"], body).ConfigureAwait(false);
                    req.Http.Response.StatusCode = result.StatusCode;
                    return result;
                }
                catch (Exception ex) when (TryMapException(req, ex, out ApiErrorResponse error)) { return error; }
            }, api => api.WithTag("Ask Threads").WithSummary("Send an idempotent saved-thread turn")
                .WithRequestBody(OpenApiJson.BodyFor<AskTurnSendRequest>("Stable request id and message", true))
                .WithResponse(200, OpenApiJson.For<AskTurnStart>("Completed or replayed turn"))
                .WithResponse(202, OpenApiJson.For<AskTurnStart>("Turn is still running"))
                .WithResponse(409, OpenApiJson.For<AskTurnStart>("Request conflicts with an active turn or an uncertain turn that cannot be replayed"))
                .WithSecurity("ApiKey"));
        }
    }
}
