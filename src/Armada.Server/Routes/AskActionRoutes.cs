namespace Armada.Server.Routes
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;
    using WatsonWebserver;
    using WatsonWebserver.Core;
    using WatsonWebserver.Core.OpenApi;

    /// <summary>Exact-owner human decisions and observations for saved chat actions.</summary>
    public sealed partial class AskActionRoutes
    {
        private readonly AskActionService _Actions;
        private static readonly JsonSerializerOptions _BodyOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        /// <summary>Create the route group.</summary>
        public AskActionRoutes(AskActionService actions) => _Actions = actions ?? throw new ArgumentNullException(nameof(actions));

        /// <summary>Register saved-action routes under the existing Ask administrator prefix.</summary>
        public void Register(Webserver app, Func<HttpContextBase, Task<AuthContext>> authenticate, IAuthorizationService authz)
        {
            RegisterReads(app, authenticate, authz);
            RegisterDecisions(app, authenticate, authz);
        }

        private void RegisterReads(Webserver app, Func<HttpContextBase, Task<AuthContext>> authenticate, IAuthorizationService authz)
        {
            app.Get("/api/v1/ask/threads/{id}/proposals", async req =>
            {
                AuthContext caller = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(caller, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery)) return RouteAuthRefusal.Refuse(req, caller);
                try
                {
                    List<AskActionProposal>? proposals = await _Actions.ListAsync(caller, req.Parameters["id"]).ConfigureAwait(false);
                    return proposals == null ? Error(req, 404, ApiResultEnum.NotFound, "Conversation not found.") : (object)proposals;
                }
                catch (Exception ex) when (TryMapException(req, ex, out ApiErrorResponse error)) { return error; }
            }, api => api.WithTag("Ask Actions").WithSummary("Read owned action proposals")
                .WithResponse(200, OpenApiJson.For<AskActionProposal[]>("Current saved proposals")).WithSecurity("ApiKey"));

            app.Get("/api/v1/ask/threads/{id}/work", async req =>
            {
                AuthContext caller = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(caller, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery)) return RouteAuthRefusal.Refuse(req, caller);
                try
                {
                    List<AskWorkReport>? work = await _Actions.WorkReportsAsync(caller, req.Parameters["id"]).ConfigureAwait(false);
                    return work == null ? Error(req, 404, ApiResultEnum.NotFound, "Conversation not found.") : (object)work;
                }
                catch (Exception ex) when (TryMapException(req, ex, out ApiErrorResponse error)) { return error; }
            }, api => api.WithTag("Ask Actions").WithSummary("Read current linked work outcomes")
                .WithResponse(200, OpenApiJson.For<AskWorkReport[]>("Authoritative work observations")).WithSecurity("ApiKey"));
        }

        private static ApiErrorResponse Error(ApiRequest req, int status, ApiResultEnum code, string message)
        {
            req.Http.Response.StatusCode = status;
            return new ApiErrorResponse { Error = code, Message = message };
        }

        private static bool TryMapException(ApiRequest req, Exception ex, out ApiErrorResponse error)
        {
            if (ex is UnauthorizedAccessException) { error = Error(req, 403, ApiResultEnum.Forbidden, "This action requires the conversation's current human owner."); return true; }
            if (ex is JsonException || ex is ArgumentException) { error = Error(req, 400, ApiResultEnum.BadRequest, "The decision request is invalid."); return true; }
            error = null!;
            return false;
        }
    }
}
