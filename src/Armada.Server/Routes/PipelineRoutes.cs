namespace Armada.Server.Routes
{
    using System;
    using System.Diagnostics;
    using System.Text.Json;
    using WatsonWebserver;
    using WatsonWebserver.Core;
    using WatsonWebserver.Core.OpenApi;
    using Armada.Server;
    using Armada.Core;
    using Armada.Core.Database;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;

    /// <summary>
    /// REST API routes for pipeline management.
    /// </summary>
    public class PipelineRoutes
    {
        private readonly DatabaseDriver _database;
        private readonly JsonSerializerOptions _jsonOptions;

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="jsonOptions">JSON serializer options.</param>
        public PipelineRoutes(
            DatabaseDriver database,
            JsonSerializerOptions jsonOptions)
        {
            _database = database ?? throw new ArgumentNullException(nameof(database));
            _jsonOptions = jsonOptions ?? throw new ArgumentNullException(nameof(jsonOptions));
        }

        /// <summary>
        /// Register routes with the application.
        /// </summary>
        /// <param name="app">Webserver.</param>
        /// <param name="authenticate">Authentication middleware.</param>
        /// <param name="authz">Authorization service.</param>
        public void Register(
            Webserver app,
            Func<WatsonWebserver.Core.HttpContextBase, Task<AuthContext>> authenticate,
            IAuthorizationService authz)
        {
            // List all pipelines
            app.Get("/api/v1/pipelines", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(ctx, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery))
                {
                    return RouteAuthRefusal.Refuse(req, ctx);
                }
                EnumerationQuery query = new EnumerationQuery();
                query.ApplyQuerystringOverrides(key => QueryValueReader.Read(req, key));
                Stopwatch sw = Stopwatch.StartNew();
                // Paging runs over the records this caller may read, so totals never count a hidden record.
                EnumerationResult<Pipeline> result = Armada.Core.Services.OwnedRecordScope.Page(
                    await _database.Pipelines.EnumerateAsync().ConfigureAwait(false), ctx, query);
                result.TotalMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2);
                return result;
            },
            api => api
                .WithTag("Pipelines")
                .WithSummary("List all pipelines")
                .WithDescription("Returns all pipelines including their stages with optional querystring filtering.")
                .WithResponse(200, OpenApiJson.For<EnumerationResult<Pipeline>>("Paginated pipeline list"))
                .WithSecurity("ApiKey"));

            // Enumerate pipelines
            app.Post<EnumerationQuery>("/api/v1/pipelines/enumerate", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(ctx, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery))
                {
                    return RouteAuthRefusal.Refuse(req, ctx);
                }
                EnumerationQuery query = JsonSerializer.Deserialize<EnumerationQuery>(req.Http.Request.DataAsString, _jsonOptions) ?? new EnumerationQuery();
                query.ApplyQuerystringOverrides(key => QueryValueReader.Read(req, key));
                Stopwatch sw = Stopwatch.StartNew();
                // Paging runs over the records this caller may read, so totals never count a hidden record.
                EnumerationResult<Pipeline> result = Armada.Core.Services.OwnedRecordScope.Page(
                    await _database.Pipelines.EnumerateAsync().ConfigureAwait(false), ctx, query);
                result.TotalMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2);
                return result;
            },
            api => api
                .WithTag("Pipelines")
                .WithSummary("Enumerate pipelines")
                .WithDescription("Paginated enumeration of pipelines with optional filtering and sorting.")
                .WithRequestBody(OpenApiJson.BodyFor<EnumerationQuery>("Enumeration query", false))
                .WithSecurity("ApiKey"));

            // Get pipeline by name
            app.Get("/api/v1/pipelines/{name}", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(ctx, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery))
                {
                    return RouteAuthRefusal.Refuse(req, ctx);
                }
                string name = req.Parameters["name"];
                Pipeline? pipeline = await Armada.Core.Services.OwnedRecordScope.ReadByNameAsync(
                    ctx,
                    name,
                    (tenantId, pipelineName) => _database.Pipelines.ReadByNameAsync(tenantId, pipelineName),
                    () => _database.Pipelines.EnumerateAsync(),
                    record => record.Name).ConfigureAwait(false);
                if (pipeline == null) { req.Http.Response.StatusCode = 404; return new ApiErrorResponse { Error = ApiResultEnum.NotFound, Message = "Pipeline not found" }; }
                return (object)pipeline;
            },
            api => api
                .WithTag("Pipelines")
                .WithSummary("Get a pipeline by name")
                .WithDescription("Returns a single pipeline by its unique name, including its stages.")
                .WithParameter(OpenApiParameterMetadata.Path("name", "Pipeline name (e.g. WorkerOnly, FullPipeline)"))
                .WithResponse(200, OpenApiJson.For<Pipeline>("Pipeline details with stages"))
                .WithResponse(404, OpenApiResponseMetadata.NotFound())
                .WithSecurity("ApiKey"));

            // Create pipeline
            app.Post<Pipeline>("/api/v1/pipelines", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(ctx, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery))
                {
                    return RouteAuthRefusal.Refuse(req, ctx);
                }
                Pipeline pipeline = JsonSerializer.Deserialize<Pipeline>(req.Http.Request.DataAsString, _jsonOptions)
                    ?? throw new InvalidOperationException("Request body could not be deserialized as Pipeline.");
                // Ownership comes from the caller, never from the body. Built-in records are
                // seeded by the server, so a request cannot create one.
                pipeline.TenantId = ctx.TenantId;
                pipeline.UserId = ctx.UserId;
                pipeline.IsBuiltIn = false;
                pipeline = await _database.Pipelines.CreateAsync(pipeline).ConfigureAwait(false);
                req.Http.Response.StatusCode = 201;
                return pipeline;
            },
            api => api
                .WithTag("Pipelines")
                .WithSummary("Create a pipeline")
                .WithDescription("Creates a new pipeline with stages defining the persona workflow.")
                .WithRequestBody(OpenApiJson.BodyFor<Pipeline>("Pipeline data (Name, Description, Stages array)", true))
                .WithResponse(201, OpenApiJson.For<Pipeline>("Created pipeline"))
                .WithSecurity("ApiKey"));

            // Update pipeline by name
            app.Put<Pipeline>("/api/v1/pipelines/{name}", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(ctx, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery))
                {
                    return RouteAuthRefusal.Refuse(req, ctx);
                }
                string name = req.Parameters["name"];
                // A global administrator reaches every tenant; anyone else stays inside their own.
                Pipeline? existing = ctx.IsAdmin
                    ? await _database.Pipelines.ReadByNameAsync(name).ConfigureAwait(false)
                    : await _database.Pipelines.ReadByNameAsync(ctx.TenantId!, name).ConfigureAwait(false);
                if (existing == null || !Armada.Core.Authorization.OwnershipPolicy.CanView(ctx, existing)) { req.Http.Response.StatusCode = 404; return new ApiErrorResponse { Error = ApiResultEnum.NotFound, Message = "Pipeline not found" }; }
                // Every tenant uses a built-in pipeline, so only a global administrator may change it.
                if (!Armada.Core.Authorization.OwnershipPolicy.CanEdit(ctx, existing)) return RouteAuthRefusal.Forbid(req, existing.IsBuiltIn ? "Built-in pipelines can be changed only by a global administrator" : "You may not change this pipeline");
                Pipeline body = JsonSerializer.Deserialize<Pipeline>(req.Http.Request.DataAsString, _jsonOptions)
                    ?? throw new InvalidOperationException("Request body could not be deserialized as Pipeline.");
                if (body.Description != null) existing.Description = body.Description;
                if (body.Stages != null && body.Stages.Count > 0) existing.Stages = body.Stages;
                existing.LastUpdateUtc = DateTime.UtcNow;
                Pipeline updated = await _database.Pipelines.UpdateAsync(existing).ConfigureAwait(false);
                return (object)updated;
            },
            api => api
                .WithTag("Pipelines")
                .WithSummary("Update a pipeline")
                .WithDescription("Updates an existing pipeline by name. Replaces stages if provided. Every tenant uses a built-in pipeline, so only a global administrator may change one; any other caller receives 403.")
                .WithParameter(OpenApiParameterMetadata.Path("name", "Pipeline name (e.g. WorkerOnly, FullPipeline)"))
                .WithRequestBody(OpenApiJson.BodyFor<Pipeline>("Updated pipeline data", true))
                .WithResponse(200, OpenApiJson.For<Pipeline>("Updated pipeline"))
                .WithResponse(403, OpenApiResponseMetadata.Forbidden())
                .WithResponse(404, OpenApiResponseMetadata.NotFound())
                .WithSecurity("ApiKey"));

            // Delete pipeline by name
            app.Delete("/api/v1/pipelines/{name}", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(ctx, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery))
                {
                    return RouteAuthRefusal.Refuse(req, ctx);
                }
                string name = req.Parameters["name"];
                // A global administrator reaches every tenant; anyone else stays inside their own.
                Pipeline? existing = ctx.IsAdmin
                    ? await _database.Pipelines.ReadByNameAsync(name).ConfigureAwait(false)
                    : await _database.Pipelines.ReadByNameAsync(ctx.TenantId!, name).ConfigureAwait(false);
                if (existing == null || (!existing.IsBuiltIn && !Armada.Core.Authorization.OwnershipPolicy.CanEdit(ctx, existing))) { req.Http.Response.StatusCode = 404; return new ApiErrorResponse { Error = ApiResultEnum.NotFound, Message = "Pipeline not found" }; }
                if (existing.IsBuiltIn) { req.Http.Response.StatusCode = 400; return new ApiErrorResponse { Error = ApiResultEnum.BadRequest, Message = "Built-in pipelines cannot be deleted" }; }
                await _database.Pipelines.DeleteAsync(existing.Id).ConfigureAwait(false);
                req.Http.Response.StatusCode = 204;
                return null;
            },
            api => api
                .WithTag("Pipelines")
                .WithSummary("Delete a pipeline")
                .WithDescription("Deletes a pipeline by name. Built-in pipelines cannot be deleted.")
                .WithParameter(OpenApiParameterMetadata.Path("name", "Pipeline name (e.g. WorkerOnly, FullPipeline)"))
                .WithResponse(204, OpenApiResponseMetadata.NoContent())
                .WithResponse(400, OpenApiResponseMetadata.BadRequest())
                .WithResponse(404, OpenApiResponseMetadata.NotFound())
                .WithSecurity("ApiKey"));
        }
    }
}
