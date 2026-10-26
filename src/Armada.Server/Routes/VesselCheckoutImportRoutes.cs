namespace Armada.Server.Routes
{
    using System;
    using System.Threading.Tasks;
    using Armada.Server;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;
    using WatsonWebserver;
    using WatsonWebserver.Core;
    using WatsonWebserver.Core.OpenApi;

    /// <summary>REST endpoints for importing a local vessel checkout.</summary>
    public sealed class VesselCheckoutImportRoutes
    {
        private readonly VesselCheckoutImportService? _Service;

        /// <summary>Create the local checkout route group.</summary>
        /// <param name="service">Shared local checkout discovery and import service.</param>
        public VesselCheckoutImportRoutes(VesselCheckoutImportService? service)
        {
            _Service = service;
        }

        /// <summary>Register local checkout discovery and import endpoints.</summary>
        /// <param name="app">REST server.</param>
        /// <param name="authenticate">Request authentication callback.</param>
        /// <param name="authz">Route authorization service.</param>
        public void Register(Webserver app, Func<HttpContextBase, Task<AuthContext>> authenticate, IAuthorizationService authz)
        {
            app.Get("/api/v1/vessels/local-checkouts", async req =>
            {
                AuthContext caller = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(caller, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery)
                    || !VesselCheckoutImportService.IsUnscopedGlobalAdminCaller(caller))
                    return RouteAuthRefusal.Refuse(req, caller, "Local checkout discovery requires a global administrator operator");
                if (_Service == null)
                {
                    req.Http.Response.StatusCode = 503;
                    return new ApiErrorResponse { Error = ApiResultEnum.InternalError, Message = "Unavailable: local checkout import is not available on this host." };
                }
                try
                {
                    return (object)await _Service.DiscoverAsync(caller).ConfigureAwait(false);
                }
                catch (UnauthorizedAccessException)
                {
                    req.Http.Response.StatusCode = 403;
                    return new ApiErrorResponse { Error = ApiResultEnum.Forbidden, Message = "Local checkout discovery requires a global administrator operator." };
                }
            }, api => api.WithTag("Vessels").WithSummary("Discover local vessel checkouts")
                .WithResponse(200, OpenApiJson.For<VesselCheckoutDiscovery>("Configured local checkout candidates"))
                .WithResponse(403, OpenApiJson.For<ApiErrorResponse>("Human global administrator required"))
                .WithResponse(503, OpenApiJson.For<ApiErrorResponse>("Local checkout import service unavailable"))
                .WithSecurity("ApiKey"));

            app.Post("/api/v1/vessels/import-checkout", async req =>
            {
                AuthContext caller = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(caller, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery)
                    || !VesselCheckoutImportService.IsUnscopedGlobalAdminCaller(caller))
                    return RouteAuthRefusal.Refuse(req, caller, "Local checkout import requires a global administrator operator");
                if (_Service == null)
                {
                    req.Http.Response.StatusCode = 503;
                    return new ApiErrorResponse { Error = ApiResultEnum.InternalError, Message = "Unavailable: local checkout import is not available on this host." };
                }
                VesselCheckoutImportRequest request;
                try
                {
                    request = req.GetData<VesselCheckoutImportRequest>();
                }
                catch (Exception ex) when (ex is ArgumentException || ex is System.Text.Json.JsonException)
                {
                    req.Http.Response.StatusCode = 400;
                    return new ApiErrorResponse { Error = ApiResultEnum.BadRequest, Message = "The local checkout import request is invalid." };
                }
                try
                {
                    return (object)await _Service.ImportAsync(caller, request).ConfigureAwait(false);
                }
                catch (UnauthorizedAccessException)
                {
                    req.Http.Response.StatusCode = 403;
                    return new ApiErrorResponse { Error = ApiResultEnum.Forbidden, Message = "Local checkout import requires a global administrator operator." };
                }
                catch (ArgumentException)
                {
                    req.Http.Response.StatusCode = 400;
                    return new ApiErrorResponse { Error = ApiResultEnum.BadRequest, Message = "The local checkout path or import request is invalid." };
                }
                catch (VesselCheckoutImportUnavailableException ex)
                {
                    req.Http.Response.StatusCode = 503;
                    return new ApiErrorResponse { Error = ApiResultEnum.InternalError, Message = ex.Message };
                }
                catch (InvalidOperationException)
                {
                    req.Http.Response.StatusCode = 409;
                    return new ApiErrorResponse { Error = ApiResultEnum.Conflict, Message = "The local checkout conflicts with an existing vessel." };
                }
            }, api => api.WithTag("Vessels").WithSummary("Import a local vessel checkout")
                .WithRequestBody(OpenApiJson.BodyFor<VesselCheckoutImportRequest>("Checkout path and vessel details", true))
                .WithResponse(200, OpenApiJson.For<Vessel>("Imported vessel"))
                .WithResponse(400, OpenApiJson.For<ApiErrorResponse>("Invalid request or checkout path"))
                .WithResponse(403, OpenApiJson.For<ApiErrorResponse>("Human global administrator required"))
                .WithResponse(409, OpenApiJson.For<ApiErrorResponse>("Vessel conflict"))
                .WithResponse(503, OpenApiJson.For<ApiErrorResponse>("Import service unavailable"))
                .WithSecurity("ApiKey"));
        }

    }
}
