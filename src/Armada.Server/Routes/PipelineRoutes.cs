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
    using Armada.Core.Services;
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
            app.Post<PipelineWriteRequest>("/api/v1/pipelines", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(ctx, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery))
                {
                    return RouteAuthRefusal.Refuse(req, ctx);
                }
                if (!RecordWriteResponse.TryReadBody(req, _jsonOptions, out PipelineWriteRequest? body, out object? refusal)) return refusal;
                RecordWriteResult<Pipeline> result = await new PipelineService(_database).CreateAsync(ctx, body).ConfigureAwait(false);
                return RecordWriteResponse.From(req, result, 201);
            },
            api => api
                .WithTag("Pipelines")
                .WithSummary("Create a pipeline")
                .WithDescription("Creates a new pipeline with stages defining the persona workflow. Name and a non-empty Stages list are required; each stage needs a PersonaName and may set IsOptional, RequiresReview, ReviewDenyAction, Description and PreferredModel. A list without orders runs in list order (1..n); a list that gives every stage an Order keeps it, and stages sharing an order run as parallel siblings; a list that orders only some stages is refused. Ownership, identifiers and timestamps come from the server, never the body. A name already used in the caller's tenant returns 409.")
                .WithRequestBody(OpenApiJson.BodyFor<PipelineWriteRequest>("Pipeline data (Name, Description, Stages array, Active, OwnershipScope)", true))
                .WithResponse(201, OpenApiJson.For<Pipeline>("Created pipeline"))
                .WithResponse(400, OpenApiResponseMetadata.BadRequest())
                .WithResponse(409, OpenApiJson.For<ApiErrorResponse>("A pipeline with that name already exists"))
                .WithSecurity("ApiKey"));

            // Update pipeline by name
            app.Put<PipelineWriteRequest>("/api/v1/pipelines/{name}", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(ctx, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery))
                {
                    return RouteAuthRefusal.Refuse(req, ctx);
                }
                if (!RecordWriteResponse.TryReadBody(req, _jsonOptions, out PipelineWriteRequest? body, out object? refusal)) return refusal;
                RecordWriteResult<Pipeline> result = await new PipelineService(_database).UpdateAsync(ctx, req.Parameters["name"], body).ConfigureAwait(false);
                return RecordWriteResponse.From(req, result, 200);
            },
            api => api
                .WithTag("Pipelines")
                .WithSummary("Update a pipeline")
                .WithDescription("Updates an existing pipeline by name. Only supplied fields change. A non-empty Stages list replaces the stages; an empty list is refused. A stage field left out keeps the value of the existing stage for the same persona, so an update that does not name RequiresReview keeps the review gate. Every tenant uses a built-in pipeline, so only a global administrator may change one; any other caller receives 403.")
                .WithParameter(OpenApiParameterMetadata.Path("name", "Pipeline name (e.g. WorkerOnly, FullPipeline)"))
                .WithRequestBody(OpenApiJson.BodyFor<PipelineWriteRequest>("Updated pipeline data", true))
                .WithResponse(400, OpenApiResponseMetadata.BadRequest())
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
                RecordWriteResult<Pipeline> result = await new PipelineService(_database).DeleteAsync(ctx, req.Parameters["name"]).ConfigureAwait(false);
                return RecordWriteResponse.From(req, result, 204);
            },
            api => api
                .WithTag("Pipelines")
                .WithSummary("Delete a pipeline")
                .WithDescription("Deletes a pipeline by name. Built-in pipelines cannot be deleted (400); a pipeline the caller may read but not change returns 403.")
                .WithResponse(403, OpenApiResponseMetadata.Forbidden())
                .WithParameter(OpenApiParameterMetadata.Path("name", "Pipeline name (e.g. WorkerOnly, FullPipeline)"))
                .WithResponse(204, OpenApiResponseMetadata.NoContent())
                .WithResponse(400, OpenApiResponseMetadata.BadRequest())
                .WithResponse(404, OpenApiResponseMetadata.NotFound())
                .WithSecurity("ApiKey"));
        }
    }
}
