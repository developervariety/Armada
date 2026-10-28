namespace Armada.Server.Routes
{
    using System;
    using System.Text.Json;
    using System.Threading.Tasks;
    using WatsonWebserver;
    using WatsonWebserver.Core;
    using WatsonWebserver.Core.OpenApi;
    using Armada.Server;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Storage;

    /// <summary>
    /// REST API routes for the administrator storage inventory: a cached, read-only measurement of
    /// Armada's on-disk data and database. Both routes require a global administrator. The inventory
    /// has no delete or cleanup route.
    /// </summary>
    public class StorageRoutes
    {
        #region Private-Members

        private readonly StorageInventoryService _Inventory;
        private readonly JsonSerializerOptions _JsonOptions;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="inventory">Storage inventory service.</param>
        /// <param name="jsonOptions">JSON serializer options.</param>
        public StorageRoutes(StorageInventoryService inventory, JsonSerializerOptions jsonOptions)
        {
            _Inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
            _JsonOptions = jsonOptions ?? throw new ArgumentNullException(nameof(jsonOptions));
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Register routes with the application.
        /// </summary>
        /// <param name="app">Webserver.</param>
        /// <param name="authenticate">Authentication middleware.</param>
        /// <param name="authz">Authorization service.</param>
        public void Register(
            Webserver app,
            Func<HttpContextBase, Task<AuthContext>> authenticate,
            IAuthorizationService authz)
        {
            app.Get("/api/v1/storage", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(ctx, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery))
                {
                    return RouteAuthRefusal.Refuse(req, ctx);
                }
                return (object)_Inventory.Read();
            },
            api => api
                .WithTag("Storage")
                .WithSummary("Read the cached storage inventory")
                .WithDescription("Returns the cached, read-only inventory of Armada's on-disk data by category (database files, logs, mission logs, docks, repositories, backups, archive staging, code index, operator evidence and the remainder of the data directory), the database size and table metadata, and the growth history. It never starts a scan. A partial figure names its reason in Warnings or PartialReasons. Requires a global administrator.")
                .WithSecurity("ApiKey"));

            app.Post("/api/v1/storage/refresh", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(ctx, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery))
                {
                    return RouteAuthRefusal.Refuse(req, ctx);
                }

                StorageRefreshRequest request;
                try
                {
                    string body = req.Http.Request.DataAsString;
                    request = String.IsNullOrWhiteSpace(body)
                        ? new StorageRefreshRequest()
                        : JsonSerializer.Deserialize<StorageRefreshRequest>(body, _JsonOptions) ?? new StorageRefreshRequest();
                }
                catch (JsonException)
                {
                    req.Http.Response.StatusCode = 400;
                    return new ApiErrorResponse { Error = ApiResultEnum.BadRequest, Message = "Expected a storage refresh request with an optional ExactCounts boolean." };
                }

                _Inventory.RequestRefresh(request.ExactCounts);
                req.Http.Response.StatusCode = 202;
                return (object)_Inventory.Read();
            },
            api => api
                .WithTag("Storage")
                .WithSummary("Request a storage inventory refresh")
                .WithDescription("Starts a bounded background collection and returns at once with the current cache; poll GET /api/v1/storage until Collecting is false. A request made while a collection runs joins that collection. Optional body: {\"ExactCounts\":true} counts every table's rows with a bounded query. Requires a global administrator.")
                .WithRequestBody(OpenApiJson.BodyFor<StorageRefreshRequest>("Optional exact record-count request", false))
                .WithResponse(202, OpenApiResponseMetadata.Create("Collection accepted or already running"))
                .WithResponse(400, OpenApiResponseMetadata.BadRequest())
                .WithSecurity("ApiKey"));
        }

        #endregion
    }
}
