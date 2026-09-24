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
    /// REST API routes for persona management.
    /// </summary>
    public class PersonaRoutes
    {
        private readonly DatabaseDriver _database;
        private readonly JsonSerializerOptions _jsonOptions;

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="jsonOptions">JSON serializer options.</param>
        public PersonaRoutes(
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
            // List all personas
            app.Get("/api/v1/personas", async (ApiRequest req) =>
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
                EnumerationResult<Persona> result = Armada.Core.Services.OwnedRecordScope.Page(
                    await _database.Personas.EnumerateAsync().ConfigureAwait(false), ctx, query);
                result.TotalMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2);
                return result;
            },
            api => api
                .WithTag("Personas")
                .WithSummary("List all personas")
                .WithDescription("Returns all personas with optional querystring filtering.")
                .WithResponse(200, OpenApiJson.For<EnumerationResult<Persona>>("Paginated persona list"))
                .WithSecurity("ApiKey"));

            // Enumerate personas
            app.Post<EnumerationQuery>("/api/v1/personas/enumerate", async (ApiRequest req) =>
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
                EnumerationResult<Persona> result = Armada.Core.Services.OwnedRecordScope.Page(
                    await _database.Personas.EnumerateAsync().ConfigureAwait(false), ctx, query);
                result.TotalMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2);
                return result;
            },
            api => api
                .WithTag("Personas")
                .WithSummary("Enumerate personas")
                .WithDescription("Paginated enumeration of personas with optional filtering and sorting.")
                .WithRequestBody(OpenApiJson.BodyFor<EnumerationQuery>("Enumeration query", false))
                .WithSecurity("ApiKey"));

            // Get persona by name
            app.Get("/api/v1/personas/{name}", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(ctx, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery))
                {
                    return RouteAuthRefusal.Refuse(req, ctx);
                }
                string name = req.Parameters["name"];
                Persona? persona = await Armada.Core.Services.OwnedRecordScope.ReadByNameAsync(
                    ctx,
                    name,
                    (tenantId, personaName) => _database.Personas.ReadByNameAsync(tenantId, personaName),
                    () => _database.Personas.EnumerateAsync(),
                    record => record.Name).ConfigureAwait(false);
                if (persona == null) { req.Http.Response.StatusCode = 404; return new ApiErrorResponse { Error = ApiResultEnum.NotFound, Message = "Persona not found" }; }
                return (object)persona;
            },
            api => api
                .WithTag("Personas")
                .WithSummary("Get a persona by name")
                .WithDescription("Returns a single persona by its unique name.")
                .WithParameter(OpenApiParameterMetadata.Path("name", "Persona name (e.g. Worker, Architect)"))
                .WithResponse(200, OpenApiJson.For<Persona>("Persona details"))
                .WithResponse(404, OpenApiResponseMetadata.NotFound())
                .WithSecurity("ApiKey"));

            // Create persona
            app.Post<PersonaWriteRequest>("/api/v1/personas", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(ctx, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery))
                {
                    return RouteAuthRefusal.Refuse(req, ctx);
                }
                if (!RecordWriteResponse.TryReadBody(req, _jsonOptions, out PersonaWriteRequest? body, out object? refusal)) return refusal;
                RecordWriteResult<Persona> result = await new PersonaService(_database).CreateAsync(ctx, body).ConfigureAwait(false);
                return RecordWriteResponse.From(req, result, 201);
            },
            api => api
                .WithTag("Personas")
                .WithSummary("Create a persona")
                .WithDescription("Creates a new persona. Name and PromptTemplateName are required. Only the allow-listed fields are read; ownership, identifiers, built-in status and timestamps come from the server. A DefaultCaptainId must name a captain in the caller's tenant whose AllowedPersonas admit the persona: otherwise 400 default_captain_not_found or default_captain_persona_locked, and nothing is created.")
                .WithRequestBody(OpenApiJson.BodyFor<PersonaWriteRequest>("Persona data (Name, PromptTemplateName, Description, MinimumTier, DefaultCaptainId, DefaultPlaybooks, Active, OwnershipScope)", true))
                .WithResponse(201, OpenApiJson.For<Persona>("Created persona"))
                .WithResponse(409, OpenApiJson.For<ApiErrorResponse>("A persona with that name already exists"))
                .WithResponse(400, OpenApiResponseMetadata.BadRequest())
                .WithSecurity("ApiKey"));

            // Update persona by name
            app.Put<PersonaWriteRequest>("/api/v1/personas/{name}", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(ctx, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery))
                {
                    return RouteAuthRefusal.Refuse(req, ctx);
                }
                if (!RecordWriteResponse.TryReadBody(req, _jsonOptions, out PersonaWriteRequest? body, out object? refusal)) return refusal;
                RecordWriteResult<Persona> result = await new PersonaService(_database).UpdateAsync(ctx, req.Parameters["name"], body).ConfigureAwait(false);
                return RecordWriteResponse.From(req, result, 200);
            },
            api => api
                .WithTag("Personas")
                .WithSummary("Update a persona")
                .WithDescription("Updates an existing persona by name. Only supplied fields are updated: Description (empty clears it), PromptTemplateName, MinimumTier (Economy, Standard, Premium, or null to clear), DefaultCaptainId (null or empty clears it), DefaultPlaybooks (an array, or the JSON text a stored persona carries; empty clears them) and Active. A DefaultCaptainId that names no captain in the persona's tenant returns 400 default_captain_not_found; a captain whose AllowedPersonas excludes the persona returns 400 default_captain_persona_locked. Every tenant uses a built-in persona, so only a global administrator may change one; any other caller receives 403.")
                .WithParameter(OpenApiParameterMetadata.Path("name", "Persona name (e.g. Worker, Architect)"))
                .WithRequestBody(OpenApiJson.BodyFor<PersonaWriteRequest>("Updated persona data", true))
                .WithResponse(200, OpenApiJson.For<Persona>("Updated persona"))
                .WithResponse(403, OpenApiResponseMetadata.Forbidden())
                .WithResponse(400, OpenApiResponseMetadata.BadRequest())
                .WithResponse(404, OpenApiResponseMetadata.NotFound())
                .WithSecurity("ApiKey"));

            // Delete persona by name
            app.Delete("/api/v1/personas/{name}", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(ctx, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery))
                {
                    return RouteAuthRefusal.Refuse(req, ctx);
                }
                RecordWriteResult<Persona> result = await new PersonaService(_database).DeleteAsync(ctx, req.Parameters["name"]).ConfigureAwait(false);
                return RecordWriteResponse.From(req, result, 204);
            },
            api => api
                .WithTag("Personas")
                .WithSummary("Delete a persona")
                .WithDescription("Deletes a persona by name. Built-in personas cannot be deleted (400); a persona the caller may read but not change returns 403.")
                .WithResponse(403, OpenApiResponseMetadata.Forbidden())
                .WithParameter(OpenApiParameterMetadata.Path("name", "Persona name (e.g. Worker, Architect)"))
                .WithResponse(204, OpenApiResponseMetadata.NoContent())
                .WithResponse(400, OpenApiResponseMetadata.BadRequest())
                .WithResponse(404, OpenApiResponseMetadata.NotFound())
                .WithSecurity("ApiKey"));
        }
    }
}
