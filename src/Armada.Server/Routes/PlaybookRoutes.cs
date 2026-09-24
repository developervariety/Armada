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
    using SyslogLogging;

    /// <summary>
    /// REST API routes for playbook management.
    /// </summary>
    public class PlaybookRoutes
    {
        private readonly DatabaseDriver _database;
        private readonly LoggingModule _logging;
        private readonly JsonSerializerOptions _jsonOptions;

        /// <summary>
        /// Instantiate.
        /// </summary>
        public PlaybookRoutes(DatabaseDriver database, LoggingModule logging, JsonSerializerOptions jsonOptions)
        {
            _database = database ?? throw new ArgumentNullException(nameof(database));
            _logging = logging ?? throw new ArgumentNullException(nameof(logging));
            _jsonOptions = jsonOptions ?? throw new ArgumentNullException(nameof(jsonOptions));
        }

        /// <summary>
        /// Register routes with the application.
        /// </summary>
        public void Register(
            Webserver app,
            Func<WatsonWebserver.Core.HttpContextBase, Task<AuthContext>> authenticate,
            IAuthorizationService authz)
        {
            app.Get("/api/v1/playbooks", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(ctx, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery))
                {
                    return RouteAuthRefusal.Refuse(req, ctx);
                }

                EnumerationQuery query = new EnumerationQuery();
                query.ApplyQuerystringOverrides(key => QueryValueReader.Read(req, key));
                Stopwatch sw = Stopwatch.StartNew();
                EnumerationResult<Playbook> result = ctx.IsAdmin
                    ? await _database.Playbooks.EnumerateAsync(query).ConfigureAwait(false)
                    : await _database.Playbooks.EnumerateAsync(ctx.TenantId!, query).ConfigureAwait(false);
                result.TotalMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2);
                return result;
            },
            api => api
                .WithTag("Playbooks")
                .WithSummary("List playbooks")
                .WithDescription("Returns tenant-scoped playbooks with optional filtering and pagination.")
                .WithResponse(200, OpenApiJson.For<EnumerationResult<Playbook>>("Paginated playbook list"))
                .WithSecurity("ApiKey"));

            app.Post<EnumerationQuery>("/api/v1/playbooks/enumerate", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(ctx, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery))
                {
                    return RouteAuthRefusal.Refuse(req, ctx);
                }

                EnumerationQuery query = JsonSerializer.Deserialize<EnumerationQuery>(req.Http.Request.DataAsString, _jsonOptions) ?? new EnumerationQuery();
                query.ApplyQuerystringOverrides(key => QueryValueReader.Read(req, key));
                Stopwatch sw = Stopwatch.StartNew();
                EnumerationResult<Playbook> result = ctx.IsAdmin
                    ? await _database.Playbooks.EnumerateAsync(query).ConfigureAwait(false)
                    : await _database.Playbooks.EnumerateAsync(ctx.TenantId!, query).ConfigureAwait(false);
                result.TotalMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2);
                return result;
            },
            api => api
                .WithTag("Playbooks")
                .WithSummary("Enumerate playbooks")
                .WithDescription("Paginated enumeration of tenant-scoped playbooks.")
                .WithRequestBody(OpenApiJson.BodyFor<EnumerationQuery>("Enumeration query", false))
                .WithSecurity("ApiKey"));

            app.Post<PlaybookWriteRequest>("/api/v1/playbooks", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(ctx, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery))
                {
                    return RouteAuthRefusal.Refuse(req, ctx);
                }

                if (!RecordWriteResponse.TryReadBody(req, _jsonOptions, out PlaybookWriteRequest? body, out object? refusal)) return refusal;
                RecordWriteResult<Playbook> result = await new PlaybookService(_database, _logging).CreateAsync(ctx, body).ConfigureAwait(false);
                return RecordWriteResponse.From(req, result, 201);
            },
            api => api
                .WithTag("Playbooks")
                .WithSummary("Create a playbook")
                .WithDescription("Creates a tenant-scoped markdown playbook. FileName (ending in .md, unique in the tenant) and Content are required. The id, owner and timestamps come from the server, never the body.")
                .WithRequestBody(OpenApiJson.BodyFor<PlaybookWriteRequest>("Playbook data (FileName, Content, Description, Active)", true))
                .WithResponse(201, OpenApiJson.For<Playbook>("Created playbook"))
                .WithResponse(400, OpenApiResponseMetadata.BadRequest())
                .WithResponse(409, OpenApiJson.For<ApiErrorResponse>("A playbook with that file name already exists"))
                .WithSecurity("ApiKey"));

            app.Get("/api/v1/playbooks/{id}", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(ctx, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery))
                {
                    return RouteAuthRefusal.Refuse(req, ctx);
                }

                string id = req.Parameters["id"];
                Playbook? playbook = ctx.IsAdmin
                    ? await _database.Playbooks.ReadAsync(id).ConfigureAwait(false)
                    : await _database.Playbooks.ReadAsync(ctx.TenantId!, id).ConfigureAwait(false);
                if (playbook == null)
                {
                    req.Http.Response.StatusCode = 404;
                    return new ApiErrorResponse { Error = ApiResultEnum.NotFound, Message = "Playbook not found" };
                }

                return playbook;
            },
            api => api
                .WithTag("Playbooks")
                .WithSummary("Get a playbook")
                .WithDescription("Returns a single playbook by ID.")
                .WithParameter(OpenApiParameterMetadata.Path("id", "Playbook ID (pbk_ prefix)"))
                .WithResponse(200, OpenApiJson.For<Playbook>("Playbook details"))
                .WithResponse(404, OpenApiResponseMetadata.NotFound())
                .WithSecurity("ApiKey"));

            app.Put<PlaybookWriteRequest>("/api/v1/playbooks/{id}", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(ctx, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery))
                {
                    return RouteAuthRefusal.Refuse(req, ctx);
                }

                if (!RecordWriteResponse.TryReadBody(req, _jsonOptions, out PlaybookWriteRequest? body, out object? refusal)) return refusal;
                RecordWriteResult<Playbook> result = await new PlaybookService(_database, _logging).UpdateAsync(ctx, req.Parameters["id"], body).ConfigureAwait(false);
                return RecordWriteResponse.From(req, result, 200);
            },
            api => api
                .WithTag("Playbooks")
                .WithSummary("Update a playbook")
                .WithDescription("Updates a tenant-scoped markdown playbook. Only supplied fields change; Description null or empty clears it.")
                .WithResponse(400, OpenApiResponseMetadata.BadRequest())
                .WithResponse(409, OpenApiJson.For<ApiErrorResponse>("A playbook with that file name already exists"))
                .WithParameter(OpenApiParameterMetadata.Path("id", "Playbook ID (pbk_ prefix)"))
                .WithRequestBody(OpenApiJson.BodyFor<PlaybookWriteRequest>("Updated playbook fields", true))
                .WithResponse(200, OpenApiJson.For<Playbook>("Updated playbook"))
                .WithResponse(404, OpenApiResponseMetadata.NotFound())
                .WithSecurity("ApiKey"));

            app.Delete("/api/v1/playbooks/{id}", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(ctx, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery))
                {
                    return RouteAuthRefusal.Refuse(req, ctx);
                }

                RecordWriteResult<Playbook> result = await new PlaybookService(_database, _logging).DeleteAsync(ctx, req.Parameters["id"]).ConfigureAwait(false);
                if (!result.Succeeded) return RecordWriteResponse.From(req, result, 200);
                return new { Status = "deleted", PlaybookId = result.Record!.Id };
            },
            api => api
                .WithTag("Playbooks")
                .WithSummary("Delete a playbook")
                .WithDescription("Deletes a playbook. Existing mission snapshots remain immutable.")
                .WithParameter(OpenApiParameterMetadata.Path("id", "Playbook ID (pbk_ prefix)"))
                .WithResponse(200, OpenApiJson.For<object>("Deletion result"))
                .WithResponse(404, OpenApiResponseMetadata.NotFound())
                .WithSecurity("ApiKey"));
        }
    }
}
