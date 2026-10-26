namespace Armada.Server.Routes
{
    using System;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;
    using WatsonWebserver;
    using WatsonWebserver.Core;
    using WatsonWebserver.Core.OpenApi;

    public sealed partial class FleetActionRoutes
    {
        private void RegisterRuns(Webserver app, Func<HttpContextBase, Task<AuthContext>> authenticate, IAuthorizationService authz)
        {
            app.Post("/api/v1/fleet-actions/{id}/run", req => ExecuteAsync(req, authenticate, authz,
                async caller => (object)await _Service.StartRunAsync(caller, req.Parameters["id"], ReadBody<FleetActionRunRequest>(req)).ConfigureAwait(false)),
                api => api.WithTag("Fleet Actions").WithSummary("Save a mission action run before dispatch")
                    .WithRequestBody(OpenApiJson.BodyFor<FleetActionRunRequest>("Stable request ID and selected vessel IDs", true))
                    .WithResponse(200, OpenApiJson.For<FleetActionRun>("Saved immutable run and current state")).WithSecurity("ApiKey"));
            app.Get("/api/v1/fleet-action-runs", req => ExecuteAsync(req, authenticate, authz,
                async caller => (object)await _Service.ListRunsAsync(caller).ConfigureAwait(false)),
                api => api.WithTag("Fleet Actions").WithSummary("List owned mission action runs")
                    .WithResponse(200, OpenApiJson.For<FleetActionRun[]>("Bounded owned runs")).WithSecurity("ApiKey"));
            app.Get("/api/v1/fleet-action-runs/{id}", req => ExecuteAsync(req, authenticate, authz, async caller =>
                (object?)await _Service.GetRunAsync(caller, req.Parameters["id"]).ConfigureAwait(false)
                    ?? Error(req, 404, ApiResultEnum.NotFound, "Saved run not found.")),
                api => api.WithTag("Fleet Actions").WithSummary("Read exact saved run targets and outcomes")
                    .WithResponse(200, OpenApiJson.For<FleetActionRun>("Saved run and current state")).WithSecurity("ApiKey"));
            app.Post("/api/v1/fleet-action-runs/{id}/cancel", req => ExecuteAsync(req, authenticate, authz,
                async caller => (object)await _Service.CancelRunAsync(caller, req.Parameters["id"]).ConfigureAwait(false)),
                api => api.WithTag("Fleet Actions").WithSummary("Request cancellation of an owned mission action run")
                    .WithResponse(200, OpenApiJson.For<FleetActionRun>("Recorded cancellation request")).WithSecurity("ApiKey"));
        }
    }
}
