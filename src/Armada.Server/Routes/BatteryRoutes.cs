namespace Armada.Server.Routes
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Threading.Tasks;
    using WatsonWebserver;
    using WatsonWebserver.Core;
    using WatsonWebserver.Core.OpenApi;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;

    /// <summary>
    /// Global-administrator routes of the fixture battery: list packs, start a run and read runs. Starting a run
    /// dispatches real captain work, so it exists only here and not as an MCP tool.
    /// </summary>
    public sealed class BatteryRoutes
    {
        #region Private-Members

        private readonly BatteryRunService _Service;
        private readonly Armada.Core.Database.DatabaseDriver _Database;
        private readonly JsonSerializerOptions _JsonOptions;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="service">Battery run service.</param>
        /// <param name="database">Database driver.</param>
        /// <param name="jsonOptions">Application JSON options.</param>
        /// <exception cref="ArgumentNullException">Thrown when an argument is null.</exception>
        public BatteryRoutes(BatteryRunService service, Armada.Core.Database.DatabaseDriver database, JsonSerializerOptions jsonOptions)
        {
            _Service = service ?? throw new ArgumentNullException(nameof(service));
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _JsonOptions = jsonOptions ?? throw new ArgumentNullException(nameof(jsonOptions));
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Register the routes.
        /// </summary>
        /// <param name="app">Web server.</param>
        /// <param name="authenticate">Authentication delegate.</param>
        /// <param name="authz">Authorization service.</param>
        public void Register(
            Webserver app,
            Func<HttpContextBase, Task<AuthContext>> authenticate,
            IAuthorizationService authz)
        {
            if (app == null) throw new ArgumentNullException(nameof(app));
            if (authenticate == null) throw new ArgumentNullException(nameof(authenticate));
            if (authz == null) throw new ArgumentNullException(nameof(authz));

            app.Get("/api/v1/battery/packs", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(ctx, "GET", "/api/v1/battery/packs")) return (object)RouteAuthRefusal.Refuse(req, ctx);
                return (object)_Service.ListPacks();
            },
            api => api
                .WithTag("Battery")
                .WithSummary("List battery packs")
                .WithDescription("Lists the fixture battery packs with their tasks and toolchain. A malformed pack is listed with valid false and its validation errors; only a valid pack can run.")
                .WithResponse(200, OpenApiJson.For<List<BatteryPackInfo>>("Battery packs"))
                .WithSecurity("ApiKey"));

            app.Get("/api/v1/battery/runs", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(ctx, "GET", "/api/v1/battery/runs")) return (object)RouteAuthRefusal.Refuse(req, ctx);
                int limit = Int32.TryParse(QueryValueReader.Read(req, "limit"), out int parsed) ? parsed : 50;
                return (object)await _Database.BatteryRuns.EnumerateAsync(
                    QueryValueReader.Read(req, "captainId"), QueryValueReader.Read(req, "packId"), limit, req.Http.Token).ConfigureAwait(false);
            },
            api => api
                .WithTag("Battery")
                .WithSummary("List battery runs")
                .WithDescription("Lists the newest battery runs first, each with the grade of every task reached. The attempts behind a grade are read from the task's mission with GET /api/v1/missions/{id}/attempts.")
                .WithParameter(OpenApiParameterMetadata.Query("captainId", "Only runs of this captain", false))
                .WithParameter(OpenApiParameterMetadata.Query("packId", "Only runs of this pack", false))
                .WithParameter(OpenApiParameterMetadata.Query("limit", "Most runs to return (1 to 200, default 50)", false))
                .WithResponse(200, OpenApiJson.For<List<BatteryRun>>("Battery runs"))
                .WithSecurity("ApiKey"));

            app.Get("/api/v1/battery/runs/{id}", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(ctx, "GET", "/api/v1/battery/runs")) return (object)RouteAuthRefusal.Refuse(req, ctx);
                BatteryRun? run = await _Database.BatteryRuns.ReadAsync(req.Parameters["id"] ?? String.Empty, req.Http.Token).ConfigureAwait(false);
                if (run == null)
                {
                    req.Http.Response.StatusCode = 404;
                    return (object)new ApiErrorResponse { Error = ApiResultEnum.NotFound, Message = "Battery run not found" };
                }
                return (object)run;
            },
            api => api
                .WithTag("Battery")
                .WithSummary("Get a battery run")
                .WithDescription("Returns one battery run with the grade, timings and evidence of each task reached, and the mission and voyage that ran it.")
                .WithResponse(200, OpenApiJson.For<BatteryRun>("Battery run"))
                .WithResponse(404, OpenApiJson.For<ApiErrorResponse>("Run not found"))
                .WithSecurity("ApiKey"));

            app.Post("/api/v1/battery/runs", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(ctx, "POST", "/api/v1/battery/runs")) return (object)RouteAuthRefusal.Refuse(req, ctx);
                BatteryRunRequest? request;
                try
                {
                    request = JsonSerializer.Deserialize<BatteryRunRequest>(req.Http.Request.DataAsString ?? String.Empty, _JsonOptions);
                }
                catch (JsonException ex)
                {
                    req.Http.Response.StatusCode = 400;
                    return (object)new ApiErrorResponse { Error = ApiResultEnum.BadRequest, Message = "Invalid battery run request: " + ex.Message };
                }

                if (request == null)
                {
                    req.Http.Response.StatusCode = 400;
                    return (object)new ApiErrorResponse { Error = ApiResultEnum.BadRequest, Message = "A request body is required." };
                }

                try
                {
                    BatteryRun run = await _Service.StartAsync(request, ctx.UserId, req.Http.Token).ConfigureAwait(false);
                    req.Http.Response.StatusCode = 202;
                    return (object)run;
                }
                catch (ArgumentException ex)
                {
                    req.Http.Response.StatusCode = 400;
                    return (object)new ApiErrorResponse { Error = ApiResultEnum.BadRequest, Message = ex.Message };
                }
                catch (InvalidOperationException ex)
                {
                    req.Http.Response.StatusCode = 409;
                    return (object)new ApiErrorResponse { Error = ApiResultEnum.Conflict, Message = ex.Message };
                }
            },
            api => api
                .WithTag("Battery")
                .WithSummary("Start a battery run")
                .WithDescription("Starts a run of one pack against one captain and returns the queued run (202). Each task resets the pack's fixture repository, dispatches a real evaluation voyage on the fixture vessel with landing mode None, and grades the resulting worktree on the Admiral host. The run spends the captain's tokens. createFixtureVessel creates the pack's fixture vessel when it does not exist.")
                .WithRequestBody(OpenApiJson.BodyFor<BatteryRunRequest>("Battery run request", true))
                .WithResponse(202, OpenApiJson.For<BatteryRun>("Queued battery run"))
                .WithResponse(400, OpenApiJson.For<ApiErrorResponse>("Invalid request"))
                .WithResponse(409, OpenApiJson.For<ApiErrorResponse>("The captain already has a run in progress"))
                .WithSecurity("ApiKey"));
        }

        #endregion
    }
}
