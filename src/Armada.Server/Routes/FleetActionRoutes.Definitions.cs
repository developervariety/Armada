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
        private void RegisterDefinitions(Webserver app, Func<HttpContextBase, Task<AuthContext>> authenticate, IAuthorizationService authz)
        {
            app.Get("/api/v1/fleet-actions", req => ExecuteAsync(req, authenticate, authz,
                async caller => (object)await _Service.ListAsync(caller).ConfigureAwait(false)),
                api => api.WithTag("Fleet Actions").WithSummary("List owned saved mission actions")
                    .WithResponse(200, OpenApiJson.For<FleetAction[]>("Owned definitions")).WithSecurity("ApiKey"));
            app.Post("/api/v1/fleet-actions", req => ExecuteAsync(req, authenticate, authz,
                async caller => (object)await _Service.CreateAsync(caller, ReadBody<FleetActionUpsertRequest>(req)).ConfigureAwait(false)),
                api => api.WithTag("Fleet Actions").WithSummary("Save a mission action")
                    .WithRequestBody(OpenApiJson.BodyFor<FleetActionUpsertRequest>("Mission action definition", true))
                    .WithResponse(200, OpenApiJson.For<FleetAction>("Saved definition")).WithSecurity("ApiKey"));
            app.Get("/api/v1/fleet-actions/{id}", req => ExecuteAsync(req, authenticate, authz, async caller =>
                (object?)await _Service.GetAsync(caller, req.Parameters["id"]).ConfigureAwait(false)
                    ?? Error(req, 404, ApiResultEnum.NotFound, "Saved action not found.")),
                api => api.WithTag("Fleet Actions").WithSummary("Read an owned mission action")
                    .WithResponse(200, OpenApiJson.For<FleetAction>("Saved definition")).WithSecurity("ApiKey"));
            app.Put("/api/v1/fleet-actions/{id}", req => ExecuteAsync(req, authenticate, authz,
                async caller => (object)await _Service.UpdateAsync(caller, req.Parameters["id"], ReadBody<FleetActionUpsertRequest>(req)).ConfigureAwait(false)),
                api => api.WithTag("Fleet Actions").WithSummary("Update an owned mission action")
                    .WithRequestBody(OpenApiJson.BodyFor<FleetActionUpsertRequest>("Mission action definition", true))
                    .WithResponse(200, OpenApiJson.For<FleetAction>("Saved definition")).WithSecurity("ApiKey"));
            app.Delete("/api/v1/fleet-actions/{id}", req => ExecuteAsync(req, authenticate, authz, async caller =>
                await _Service.DeleteAsync(caller, req.Parameters["id"]).ConfigureAwait(false)
                    ? (object)new { Deleted = true } : Error(req, 404, ApiResultEnum.NotFound, "Saved action not found.")),
                api => api.WithTag("Fleet Actions").WithSummary("Delete an owned mission action definition")
                    .WithResponse(200, OpenApiJson.For<object>("Deletion result; existing runs remain")).WithSecurity("ApiKey"));
        }
    }
}
