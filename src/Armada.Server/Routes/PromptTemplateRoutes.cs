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
    /// REST API routes for prompt template management.
    /// </summary>
    public class PromptTemplateRoutes
    {
        private readonly DatabaseDriver _database;
        private readonly IPromptTemplateService _promptTemplateService;
        private readonly JsonSerializerOptions _jsonOptions;

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="promptTemplateService">Prompt template service.</param>
        /// <param name="jsonOptions">JSON serializer options.</param>
        public PromptTemplateRoutes(
            DatabaseDriver database,
            IPromptTemplateService promptTemplateService,
            JsonSerializerOptions jsonOptions)
        {
            _database = database ?? throw new ArgumentNullException(nameof(database));
            _promptTemplateService = promptTemplateService ?? throw new ArgumentNullException(nameof(promptTemplateService));
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
            // List all prompt templates
            app.Get("/api/v1/prompt-templates", async (ApiRequest req) =>
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
                EnumerationResult<PromptTemplate> result = Armada.Core.Services.OwnedRecordScope.Page(
                    await _database.PromptTemplates.EnumerateAsync().ConfigureAwait(false), ctx, query);
                result.TotalMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2);
                return result;
            },
            api => api
                .WithTag("Prompt Templates")
                .WithSummary("List all prompt templates")
                .WithDescription("Returns all prompt templates with optional querystring filtering.")
                .WithResponse(200, OpenApiJson.For<EnumerationResult<PromptTemplate>>("Paginated prompt template list"))
                .WithSecurity("ApiKey"));

            // Enumerate prompt templates
            app.Post<EnumerationQuery>("/api/v1/prompt-templates/enumerate", async (ApiRequest req) =>
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
                EnumerationResult<PromptTemplate> result = Armada.Core.Services.OwnedRecordScope.Page(
                    await _database.PromptTemplates.EnumerateAsync().ConfigureAwait(false), ctx, query);
                result.TotalMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2);
                return result;
            },
            api => api
                .WithTag("Prompt Templates")
                .WithSummary("Enumerate prompt templates")
                .WithDescription("Paginated enumeration of prompt templates with optional filtering and sorting.")
                .WithRequestBody(OpenApiJson.BodyFor<EnumerationQuery>("Enumeration query", false))
                .WithSecurity("ApiKey"));

            // Create prompt template
            app.Post<PromptTemplateWriteRequest>("/api/v1/prompt-templates", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(ctx, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery))
                {
                    return RouteAuthRefusal.Refuse(req, ctx);
                }

                if (!RecordWriteResponse.TryReadBody(req, _jsonOptions, out PromptTemplateWriteRequest? body, out object? refusal)) return refusal;
                RecordWriteResult<PromptTemplate> result = await new PromptTemplateWriteService(_database).CreateAsync(ctx, body).ConfigureAwait(false);
                return RecordWriteResponse.From(req, result, 201);
            },
            api => api
                .WithTag("Prompt Templates")
                .WithSummary("Create a prompt template")
                .WithDescription("Creates a new prompt template with a unique name, category, and prompt content. Name and Category are trimmed. Only the allow-listed fields are read; ownership, built-in status and timestamps come from the server.")
                .WithRequestBody(OpenApiJson.BodyFor<PromptTemplateWriteRequest>("Prompt template data (Name, Category, Content, Description, Active, OwnershipScope)", true))
                .WithResponse(201, OpenApiJson.For<PromptTemplate>("Created prompt template"))
                .WithResponse(400, OpenApiResponseMetadata.BadRequest())
                .WithResponse(409, OpenApiJson.For<ApiErrorResponse>("A prompt template with that name already exists"))
                .WithSecurity("ApiKey"));

            // Get prompt template by name
            app.Get("/api/v1/prompt-templates/{name}", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(ctx, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery))
                {
                    return RouteAuthRefusal.Refuse(req, ctx);
                }
                string name = req.Parameters["name"];
                PromptTemplate? template = await Armada.Core.Services.OwnedRecordScope.ReadByNameAsync(
                    ctx,
                    name,
                    (tenantId, templateName) => _database.PromptTemplates.ReadByNameAsync(tenantId, templateName),
                    () => _database.PromptTemplates.EnumerateAsync(),
                    record => record.Name).ConfigureAwait(false);
                if (template == null) { req.Http.Response.StatusCode = 404; return new ApiErrorResponse { Error = ApiResultEnum.NotFound, Message = "Prompt template not found" }; }
                return (object)template;
            },
            api => api
                .WithTag("Prompt Templates")
                .WithSummary("Get a prompt template by name")
                .WithDescription("Returns a single prompt template by its unique name.")
                .WithParameter(OpenApiParameterMetadata.Path("name", "Template name (e.g. mission.rules)"))
                .WithResponse(200, OpenApiJson.For<PromptTemplate>("Prompt template details"))
                .WithResponse(404, OpenApiResponseMetadata.NotFound())
                .WithSecurity("ApiKey"));

            // Update prompt template by name
            app.Put<PromptTemplateWriteRequest>("/api/v1/prompt-templates/{name}", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(ctx, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery))
                {
                    return RouteAuthRefusal.Refuse(req, ctx);
                }

                if (!RecordWriteResponse.TryReadBody(req, _jsonOptions, out PromptTemplateWriteRequest? body, out object? refusal)) return refusal;
                RecordWriteResult<PromptTemplate> result = await new PromptTemplateWriteService(_database).UpdateAsync(ctx, req.Parameters["name"], body).ConfigureAwait(false);
                return RecordWriteResponse.From(req, result, 200);
            },
            api => api
                .WithTag("Prompt Templates")
                .WithSummary("Update a prompt template")
                .WithDescription("Updates an existing prompt template by name. Only supplied fields change: Content, Description (empty clears it), Category and Active. A missing template returns 404; an update never creates one.")
                .WithResponse(400, OpenApiResponseMetadata.BadRequest())
                .WithResponse(403, OpenApiResponseMetadata.Forbidden())
                .WithParameter(OpenApiParameterMetadata.Path("name", "Template name (e.g. mission.rules)"))
                .WithRequestBody(OpenApiJson.BodyFor<PromptTemplateWriteRequest>("Updated template fields", true))
                .WithResponse(200, OpenApiJson.For<PromptTemplate>("Updated prompt template"))
                .WithResponse(404, OpenApiResponseMetadata.NotFound())
                .WithSecurity("ApiKey"));

            // Reset prompt template to default
            app.Post("/api/v1/prompt-templates/{name}/reset", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(ctx, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery))
                {
                    return RouteAuthRefusal.Refuse(req, ctx);
                }
                string name = req.Parameters["name"];
                PromptTemplate? result = await _promptTemplateService.ResetToDefaultAsync(name).ConfigureAwait(false);
                if (result == null) { req.Http.Response.StatusCode = 404; return new ApiErrorResponse { Error = ApiResultEnum.NotFound, Message = "No embedded default exists for template '" + name + "'" }; }
                return (object)result;
            },
            api => api
                .WithTag("Prompt Templates")
                .WithSummary("Reset a prompt template to default")
                .WithDescription("Resets a prompt template to its embedded resource default content.")
                .WithParameter(OpenApiParameterMetadata.Path("name", "Template name (e.g. mission.rules)"))
                .WithResponse(200, OpenApiJson.For<PromptTemplate>("Reset prompt template"))
                .WithResponse(404, OpenApiResponseMetadata.NotFound())
                .WithSecurity("ApiKey"));
        }
    }
}
