namespace Armada.Server.Routes
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using WatsonWebserver;
    using WatsonWebserver.Core;
    using WatsonWebserver.Core.OpenApi;

    public partial class VoyageRoutes
    {
        private void RegisterAdministrativeRecovery(Webserver app, Func<HttpContextBase, Task<AuthContext>> authenticate,
            IAuthorizationService authz)
        {
            AdministrativeVoyageRecoveryService service = new AdministrativeVoyageRecoveryService(_database,
                _admiral, new GitService(_logging, database: _database), _settings,
                new OperationNotifier(null, null, voyage => _webSocketHub?.BroadcastVoyageChange(voyage)));
            app.Post("/api/v1/voyages/{id}/administrative-completion-correction", async req =>
            {
                AuthContext caller = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(caller, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery)
                    || !caller.IsAdmin || !String.IsNullOrEmpty(caller.MissionId)
                    || !String.IsNullOrEmpty(caller.AskThreadId) || !String.IsNullOrEmpty(caller.AskTurnId))
                    return RouteAuthRefusal.Refuse(req, caller);
                try
                {
                    AdministrativeVoyageRecoveryRequest request = JsonSerializer.Deserialize<AdministrativeVoyageRecoveryRequest>(
                        req.Http.Request.DataAsString, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                        ?? throw new ArgumentException("Body required.");
                    return (object)await service.ExecuteAsync(caller, req.Parameters["id"], request).ConfigureAwait(false);
                }
                catch (UnauthorizedAccessException) { return ReplayError(req, 403, ApiResultEnum.Forbidden, "Exact-owner global operator authority is required."); }
                catch (KeyNotFoundException) { return ReplayError(req, 404, ApiResultEnum.NotFound, "Voyage not found."); }
                catch (Exception ex) when (ex is ArgumentException || ex is JsonException)
                { return ReplayError(req, 400, ApiResultEnum.BadRequest, "The administrative correction request is invalid."); }
                catch (InvalidOperationException)
                { return ReplayError(req, 409, ApiResultEnum.Conflict, "Read a fresh preview and resolve its source and graph blockers; nothing changed."); }
            }, api => api.WithTag("Voyages").WithSummary("Preview or correct an evidenced planning-only administrative completion without restarting work")
                .WithRequestBody(OpenApiJson.BodyFor<AdministrativeVoyageRecoveryRequest>("Preview first; apply requires the exact current fingerprint", true))
                .WithResponse(200, OpenApiJson.For<AdministrativeVoyageRecoveryPlan>("Evidence-bound preview or correction receipt")).WithSecurity("ApiKey"));
        }
    }
}
