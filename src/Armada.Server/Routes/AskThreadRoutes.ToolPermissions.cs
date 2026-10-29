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

    public sealed partial class AskThreadRoutes
    {
        private void RegisterToolPermissionRoutes(Webserver app, Func<HttpContextBase, Task<AuthContext>> authenticate, IAuthorizationService authz)
        {
            if (_ToolPermissions == null) return;
            AskToolPermissionService permissions = _ToolPermissions;

            app.Get("/api/v1/ask/threads/{id}/tool-permissions", async (ApiRequest req) =>
            {
                AuthContext caller = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(caller, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery)) return RouteAuthRefusal.Refuse(req, caller);
                try
                {
                    List<AskToolPermissionRequest>? requests = await permissions.ListRequestsAsync(caller, req.Parameters["id"]).ConfigureAwait(false);
                    if (requests == null) return Error(req, 404, ApiResultEnum.NotFound, "Ask thread not found.");
                    return requests;
                }
                catch (Exception ex) when (TryMapException(req, ex, out ApiErrorResponse error)) { return error; }
            }, api => api.WithTag("Ask Threads").WithSummary("List native tool permission requests of a saved Ask thread")
                .WithDescription("Lists the newest requests, pending ones included, that an attended Ask turn made to use a native tool.")
                .WithParameter(OpenApiParameterMetadata.Path("id", "Ask thread ID"))
                .WithResponse(200, OpenApiJson.For<List<AskToolPermissionRequest>>("Permission requests, newest first"))
                .WithResponse(404, OpenApiResponseMetadata.NotFound())
                .WithSecurity("ApiKey"));

            app.Post<AskToolPermissionDecisionRequest>("/api/v1/ask/threads/{id}/tool-permissions/{requestId}/allow", async (ApiRequest req) =>
                await DecideToolPermissionAsync(req, authenticate, authz, permissions, true).ConfigureAwait(false),
                api => api.WithTag("Ask Threads").WithSummary("Allow a native tool request")
                    .WithDescription("Allows one pending request. With remember set, later requests for the same tool in this conversation are allowed without a prompt. Refused with 409 once the request has been decided or has expired.")
                    .WithRequestBody(OpenApiJson.BodyFor<AskToolPermissionDecisionRequest>("Optional remember flag", false))
                    .WithResponse(200, OpenApiJson.For<AskToolPermissionRequest>("Decided request"))
                    .WithResponse(404, OpenApiResponseMetadata.NotFound())
                    .WithResponse(409, OpenApiJson.For<ApiErrorResponse>("Request already decided or expired"))
                    .WithSecurity("ApiKey"));

            app.Post<AskToolPermissionDecisionRequest>("/api/v1/ask/threads/{id}/tool-permissions/{requestId}/deny", async (ApiRequest req) =>
                await DecideToolPermissionAsync(req, authenticate, authz, permissions, false).ConfigureAwait(false),
                api => api.WithTag("Ask Threads").WithSummary("Deny a native tool request")
                    .WithDescription("Denies one pending request. With remember set, later requests for the same tool in this conversation are denied without a prompt. Refused with 409 once the request has been decided or has expired.")
                    .WithRequestBody(OpenApiJson.BodyFor<AskToolPermissionDecisionRequest>("Optional remember flag", false))
                    .WithResponse(200, OpenApiJson.For<AskToolPermissionRequest>("Decided request"))
                    .WithResponse(404, OpenApiResponseMetadata.NotFound())
                    .WithResponse(409, OpenApiJson.For<ApiErrorResponse>("Request already decided or expired"))
                    .WithSecurity("ApiKey"));

            app.Get("/api/v1/ask/threads/{id}/tool-permission-rules", async (ApiRequest req) =>
            {
                AuthContext caller = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(caller, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery)) return RouteAuthRefusal.Refuse(req, caller);
                try
                {
                    List<AskToolPermissionRule>? rules = await permissions.ListRulesAsync(caller, req.Parameters["id"]).ConfigureAwait(false);
                    if (rules == null) return Error(req, 404, ApiResultEnum.NotFound, "Ask thread not found.");
                    return rules;
                }
                catch (Exception ex) when (TryMapException(req, ex, out ApiErrorResponse error)) { return error; }
            }, api => api.WithTag("Ask Threads").WithSummary("List remembered native tool rules of a saved Ask thread")
                .WithParameter(OpenApiParameterMetadata.Path("id", "Ask thread ID"))
                .WithResponse(200, OpenApiJson.For<List<AskToolPermissionRule>>("Remembered rules"))
                .WithResponse(404, OpenApiResponseMetadata.NotFound())
                .WithSecurity("ApiKey"));

            app.Delete("/api/v1/ask/threads/{id}/tool-permission-rules/{ruleId}", async (ApiRequest req) =>
            {
                AuthContext caller = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(caller, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery)) return RouteAuthRefusal.Refuse(req, caller);
                try
                {
                    bool removed = await permissions.DeleteRuleAsync(caller, req.Parameters["id"], req.Parameters["ruleId"]).ConfigureAwait(false);
                    if (!removed) return Error(req, 404, ApiResultEnum.NotFound, "Rule not found.");
                    req.Http.Response.StatusCode = 204;
                    return null;
                }
                catch (Exception ex) when (TryMapException(req, ex, out ApiErrorResponse error)) { return error; }
            }, api => api.WithTag("Ask Threads").WithSummary("Forget a remembered native tool rule")
                .WithParameter(OpenApiParameterMetadata.Path("id", "Ask thread ID"))
                .WithParameter(OpenApiParameterMetadata.Path("ruleId", "Rule ID"))
                .WithResponse(204, OpenApiResponseMetadata.NoContent())
                .WithResponse(404, OpenApiResponseMetadata.NotFound())
                .WithSecurity("ApiKey"));
        }

        private static async Task<object?> DecideToolPermissionAsync(ApiRequest req, Func<HttpContextBase, Task<AuthContext>> authenticate, IAuthorizationService authz, AskToolPermissionService permissions, bool allow)
        {
            AuthContext caller = await authenticate(req.Http).ConfigureAwait(false);
            if (!authz.IsAuthorized(caller, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery)) return RouteAuthRefusal.Refuse(req, caller);
            try
            {
                bool remember = !String.IsNullOrWhiteSpace(req.Http.Request.DataAsString) && Deserialize<AskToolPermissionDecisionRequest>(req).Remember;
                AskToolPermissionRequest? decided = await permissions.DecideAsync(caller, req.Parameters["id"], req.Parameters["requestId"], allow, remember).ConfigureAwait(false);
                if (decided == null) return Error(req, 404, ApiResultEnum.NotFound, "Permission request not found.");
                return decided;
            }
            catch (Exception ex) when (TryMapException(req, ex, out ApiErrorResponse error)) { return error; }
        }
    }
}
