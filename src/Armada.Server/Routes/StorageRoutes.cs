namespace Armada.Server.Routes
{
    using System;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Storage;
    using WatsonWebserver;
    using WatsonWebserver.Core;
    using WatsonWebserver.Core.OpenApi;

    /// <summary>Global-administrator-only cached storage inventory and background refresh routes.</summary>
    public class StorageRoutes
    {
        private readonly StorageInventoryService _Inventory;
        private readonly JsonSerializerOptions _JsonOptions;

        /// <summary>Construct storage routes.</summary>
        /// <exception cref="ArgumentNullException">A dependency is null.</exception>
        public StorageRoutes(StorageInventoryService inventory, JsonSerializerOptions jsonOptions)
        {
            _Inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
            _JsonOptions = jsonOptions ?? throw new ArgumentNullException(nameof(jsonOptions));
        }

        /// <summary>Register authenticated routes. Authorization is enforced centrally and in each handler.</summary>
        public void Register(Webserver app, Func<HttpContextBase, Task<AuthContext>> authenticate, IAuthorizationService authz)
        {
            app.Get("/api/v1/storage", async (ApiRequest req) =>
            {
                AuthContext context = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(context, "GET", "/api/v1/storage")) return Denied(req, context);
                return (object)_Inventory.Read();
            }, api => api.WithTag("Storage").WithSummary("Read cached storage inventory")
                .WithDescription("Global administrators only. Returns bounded cached host, filesystem, database, retention and process-local history measurements. Does not trigger scans. Logical file bytes and database allocation are separate quantities.")
                .WithResponse(200, OpenApiJson.For<StorageSnapshot>("Cached storage snapshot"))
                .WithResponse(401, OpenApiResponseMetadata.Create("Authentication required"))
                .WithResponse(403, OpenApiResponseMetadata.Create("Global administrator required"))
                .WithSecurity("ApiKey"));

            app.Post("/api/v1/storage/refresh", async (ApiRequest req) =>
            {
                AuthContext context = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(context, "POST", "/api/v1/storage/refresh")) return Denied(req, context);
                StorageRefreshRequest request;
                try
                {
                    request = String.IsNullOrWhiteSpace(req.Http.Request.DataAsString) ? new StorageRefreshRequest()
                        : JsonSerializer.Deserialize<StorageRefreshRequest>(req.Http.Request.DataAsString, _JsonOptions) ?? new StorageRefreshRequest();
                }
                catch (JsonException)
                {
                    req.Http.Response.StatusCode = 400;
                    return (object)new ApiErrorResponse { Error = ApiResultEnum.BadRequest, Message = "Expected a storage refresh request with an optional exactCounts boolean." };
                }
                _Inventory.RequestRefresh(request.ExactCounts);
                req.Http.Response.StatusCode = 202;
                return (object)_Inventory.Read();
            }, api => api.WithTag("Storage").WithSummary("Request background storage collection")
                .WithDescription("Global administrators only. Optional JSON body: {\"exactCounts\":false}. Concurrent requests coalesce. Exact counts scan database tables with timeouts. Returns immediately; poll GET /api/v1/storage for results. Harbor reports arrive on their own collection cadence.")
                .WithRequestBody(OpenApiJson.BodyFor<StorageRefreshRequest>("Optional exact record-count request", false))
                .WithResponse(202, OpenApiJson.For<StorageSnapshot>("Collection accepted or already running"))
                .WithResponse(400, OpenApiResponseMetadata.BadRequest())
                .WithResponse(401, OpenApiResponseMetadata.Create("Authentication required"))
                .WithResponse(403, OpenApiResponseMetadata.Create("Global administrator required"))
                .WithSecurity("ApiKey"));
        }

        private static object Denied(ApiRequest request, AuthContext context)
        {
            request.Http.Response.StatusCode = context.IsAuthenticated ? 403 : 401;
            return new ApiErrorResponse { Error = context.IsAuthenticated ? ApiResultEnum.Forbidden : ApiResultEnum.NotAuthorized, Message = "Global administrator authentication required." };
        }
    }
}
