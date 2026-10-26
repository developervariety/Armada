namespace Armada.Server.Routes
{
    using System;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;
    using WatsonWebserver;
    using WatsonWebserver.Core;
    using WatsonWebserver.Core.OpenApi;

    public sealed partial class AskActionRoutes
    {
        private void RegisterDecisions(Webserver app, Func<HttpContextBase, Task<AuthContext>> authenticate, IAuthorizationService authz)
        {
            app.Post<AskProposalDecisionRequest>("/api/v1/ask/threads/{id}/proposals/{proposalId}/approve",
                req => HandleDecisionAsync(req, authenticate, authz, true),
                api => api.WithTag("Ask Actions").WithSummary("Approve the exact saved action")
                    .WithRequestBody(OpenApiJson.BodyFor<AskProposalDecisionRequest>("Reviewed action fingerprint", true))
                    .WithResponse(200, OpenApiJson.For<AskProposalDecision>("Recorded human decision"))
                    .WithResponse(409, OpenApiJson.For<AskProposalDecision>("Conflicting or uncertain decision")).WithSecurity("ApiKey"));
            app.Post<AskProposalDecisionRequest>("/api/v1/ask/threads/{id}/proposals/{proposalId}/reject",
                req => HandleDecisionAsync(req, authenticate, authz, false),
                api => api.WithTag("Ask Actions").WithSummary("Reject the exact saved action")
                    .WithRequestBody(OpenApiJson.BodyFor<AskProposalDecisionRequest>("Reviewed action fingerprint", true))
                    .WithResponse(200, OpenApiJson.For<AskProposalDecision>("Recorded human decision"))
                    .WithResponse(409, OpenApiJson.For<AskProposalDecision>("Conflicting decision")).WithSecurity("ApiKey"));
        }

        private async Task<object> HandleDecisionAsync(ApiRequest req, Func<HttpContextBase, Task<AuthContext>> authenticate, IAuthorizationService authz, bool approve)
        {
            AuthContext caller = await authenticate(req.Http).ConfigureAwait(false);
            if (!authz.IsAuthorized(caller, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery)) return RouteAuthRefusal.Refuse(req, caller);
            try
            {
                AskProposalDecisionRequest body = JsonSerializer.Deserialize<AskProposalDecisionRequest>(req.Http.Request.DataAsString, _BodyOptions) ?? new AskProposalDecisionRequest();
                AskProposalDecision result = approve
                    ? await _Actions.ApproveAsync(caller, req.Parameters["id"], req.Parameters["proposalId"], body.Fingerprint).ConfigureAwait(false)
                    : await _Actions.RejectAsync(caller, req.Parameters["id"], req.Parameters["proposalId"], body.Fingerprint).ConfigureAwait(false);
                req.Http.Response.StatusCode = result.StatusCode;
                return result;
            }
            catch (Exception ex) when (TryMapException(req, ex, out ApiErrorResponse error)) { return error; }
        }
    }
}
