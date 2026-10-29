namespace Armada.Server.Routes
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Threading.Tasks;
    using WatsonWebserver;
    using WatsonWebserver.Core;
    using WatsonWebserver.Core.OpenApi;
    using Armada.Server;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;

    /// <summary>
    /// REST routes for native captain memory: list and search, create or update by key, read, change,
    /// and delete. Every route is scoped to the caller by the memory service.
    /// </summary>
    public class MemoryRoutes
    {
        private readonly MemoryService _Memories;
        private readonly JsonSerializerOptions _JsonOptions;

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="memories">Memory service.</param>
        /// <param name="jsonOptions">JSON options.</param>
        public MemoryRoutes(MemoryService memories, JsonSerializerOptions jsonOptions)
        {
            _Memories = memories ?? throw new ArgumentNullException(nameof(memories));
            _JsonOptions = jsonOptions ?? throw new ArgumentNullException(nameof(jsonOptions));
        }

        /// <summary>
        /// Register routes with the application.
        /// </summary>
        /// <param name="app">Web server.</param>
        /// <param name="authenticate">Authentication delegate.</param>
        /// <param name="authz">Authorization service.</param>
        public void Register(
            Webserver app,
            Func<HttpContextBase, Task<AuthContext>> authenticate,
            IAuthorizationService authz)
        {
            app.Get("/api/v1/memories", async (ApiRequest req) =>
            {
                AuthContext? ctx = await AuthorizeAsync(req, authenticate, authz).ConfigureAwait(false);
                if (ctx == null) return AuthError(req);

                MemoryEnumerateRequest request = new MemoryEnumerateRequest();
                request.ApplyQuerystringOverrides(key => QueryValueReader.Read(req, key));
                if (!TryApplyMemoryFilters(req, request, out string? filterError))
                {
                    req.Http.Response.StatusCode = 400;
                    return new ApiErrorResponse { Error = ApiResultEnum.BadRequest, Message = filterError };
                }

                return await _Memories.EnumerateAsync(ctx, request).ConfigureAwait(false);
            },
            api => api
                .WithTag("Memories")
                .WithSummary("List or search memories")
                .WithDescription("Returns the native memory records visible to the caller, highest salience first, then newest. Filter with type, topic, vesselId and search; page with pageNumber and pageSize.")
                .WithResponse(200, OpenApiJson.For<EnumerationResult<Memory>>("Paginated memory list"))
                .WithSecurity("ApiKey"));

            app.Post("/api/v1/memories/enumerate", async (ApiRequest req) =>
            {
                AuthContext? ctx = await AuthorizeAsync(req, authenticate, authz).ConfigureAwait(false);
                if (ctx == null) return AuthError(req);

                if (!EnumerationRequestReader.TryRead(req, _JsonOptions, out MemoryEnumerateRequest request, out string? queryError))
                {
                    req.Http.Response.StatusCode = 400;
                    return new ApiErrorResponse { Error = ApiResultEnum.BadRequest, Message = queryError };
                }

                if (!TryApplyMemoryFilters(req, request, out string? filterError))
                {
                    req.Http.Response.StatusCode = 400;
                    return new ApiErrorResponse { Error = ApiResultEnum.BadRequest, Message = filterError };
                }

                return await _Memories.EnumerateAsync(ctx, request).ConfigureAwait(false);
            },
            api => api
                .WithTag("Memories")
                .WithSummary("Enumerate memories")
                .WithDescription("Returns one page of the native memory records visible to the caller, highest salience first, then newest. The database applies visibility, filters and the page, so TotalRecords counts every visible record that matches. Filters come from the JSON body (EnumerationQuery plus search, type and topic) or the querystring, which overrides the body: pageNumber, pageSize (1 to 1000, default 10), search, type, topic, vesselId, voyageId, missionId, createdAfter, createdBefore. The order is fixed.")
                .WithRequestBody(OpenApiJson.BodyFor<MemoryEnumerateRequest>("Paging and filters", false))
                .WithResponse(200, OpenApiJson.For<EnumerationResult<Memory>>("One page of memories"))
                .WithResponse(400, OpenApiResponseMetadata.BadRequest())
                .WithSecurity("ApiKey"));

            app.Post("/api/v1/memories", async (ApiRequest req) =>
            {
                AuthContext? ctx = await AuthorizeAsync(req, authenticate, authz).ConfigureAwait(false);
                if (ctx == null) return AuthError(req);

                Memory request = JsonSerializer.Deserialize<Memory>(req.Http.Request.DataAsString, _JsonOptions)
                    ?? throw new InvalidOperationException("Request body could not be deserialized as Memory.");
                int? expectedVersion = null;
                if (Int32.TryParse(QueryValueReader.Read(req, "expectedVersion"), out int parsedVersion)) expectedVersion = parsedVersion;

                return await GuardAsync(req, async () =>
                {
                    Memory saved = await _Memories.UpsertAsync(ctx, request, expectedVersion).ConfigureAwait(false);
                    req.Http.Response.StatusCode = saved.Version == 1 ? 201 : 200;
                    return (object)saved;
                }).ConfigureAwait(false);
            },
            api => api
                .WithTag("Memories")
                .WithSummary("Create a memory, or update the record with the same key")
                .WithDescription("Creates a record. When the body carries a key that already exists in the caller's tenant, that record is updated in place and its version increases. Returns 201 on create and 200 on update.")
                .WithRequestBody(OpenApiJson.BodyFor<Memory>("Memory record", true))
                .WithResponse(201, OpenApiJson.For<Memory>("Created memory"))
                .WithResponse(409, OpenApiJson.For<object>("The record changed since it was read, or the key is taken"))
                .WithSecurity("ApiKey"));

            app.Get("/api/v1/memories/{id}", async (ApiRequest req) =>
            {
                AuthContext? ctx = await AuthorizeAsync(req, authenticate, authz).ConfigureAwait(false);
                if (ctx == null) return AuthError(req);

                Memory? memory = await _Memories.ReadAsync(ctx, req.Parameters["id"]).ConfigureAwait(false);
                if (memory == null)
                {
                    req.Http.Response.StatusCode = 404;
                    return new ApiErrorResponse { Error = ApiResultEnum.NotFound, Message = "Memory not found" };
                }

                return memory;
            },
            api => api
                .WithTag("Memories")
                .WithSummary("Get a memory")
                .WithDescription("Returns one memory record by identifier.")
                .WithParameter(OpenApiParameterMetadata.Path("id", "Memory identifier (mem_ prefix)"))
                .WithResponse(200, OpenApiJson.For<Memory>("Memory"))
                .WithResponse(404, OpenApiResponseMetadata.NotFound())
                .WithSecurity("ApiKey"));

            app.Put("/api/v1/memories/{id}", async (ApiRequest req) =>
            {
                AuthContext? ctx = await AuthorizeAsync(req, authenticate, authz).ConfigureAwait(false);
                if (ctx == null) return AuthError(req);

                MemoryUpdate request = JsonSerializer.Deserialize<MemoryUpdate>(req.Http.Request.DataAsString, _JsonOptions)
                    ?? throw new InvalidOperationException("Request body could not be deserialized as MemoryUpdate.");
                if (!request.ExpectedVersion.HasValue && Int32.TryParse(QueryValueReader.Read(req, "expectedVersion"), out int parsedVersion))
                    request.ExpectedVersion = parsedVersion;

                return await GuardAsync(req, async () =>
                {
                    Memory saved = await _Memories.UpdateAsync(ctx, req.Parameters["id"], request).ConfigureAwait(false);
                    return (object)saved;
                }).ConfigureAwait(false);
            },
            api => api
                .WithTag("Memories")
                .WithSummary("Change a memory")
                .WithDescription("Applies the supplied fields to one record and increases its version. Send expectedVersion to be refused instead of overwriting a newer record.")
                .WithParameter(OpenApiParameterMetadata.Path("id", "Memory identifier (mem_ prefix)"))
                .WithRequestBody(OpenApiJson.BodyFor<MemoryUpdate>("Fields to change", true))
                .WithResponse(200, OpenApiJson.For<Memory>("Updated memory"))
                .WithResponse(404, OpenApiResponseMetadata.NotFound())
                .WithResponse(409, OpenApiJson.For<object>("The record changed since it was read, or the key is taken"))
                .WithSecurity("ApiKey"));

            app.Delete("/api/v1/memories/{id}", async (ApiRequest req) =>
            {
                AuthContext? ctx = await AuthorizeAsync(req, authenticate, authz).ConfigureAwait(false);
                if (ctx == null) return AuthError(req);

                return await GuardAsync(req, async () =>
                {
                    await _Memories.DeleteAsync(ctx, req.Parameters["id"]).ConfigureAwait(false);
                    req.Http.Response.StatusCode = 204;
                    return (object?)null!;
                }).ConfigureAwait(false);
            },
            api => api
                .WithTag("Memories")
                .WithSummary("Delete a memory")
                .WithDescription("Deletes one record the caller may change, for example a record that went stale.")
                .WithParameter(OpenApiParameterMetadata.Path("id", "Memory identifier (mem_ prefix)"))
                .WithResponse(204, OpenApiResponseMetadata.NoContent())
                .WithResponse(404, OpenApiResponseMetadata.NotFound())
                .WithSecurity("ApiKey"));
        }

        private static async Task<object?> GuardAsync(ApiRequest req, Func<Task<object>> action)
        {
            try
            {
                return await action().ConfigureAwait(false);
            }
            catch (MemoryConflictException conflict)
            {
                req.Http.Response.StatusCode = 409;
                return new ApiErrorResponse { Error = ApiResultEnum.Conflict, Message = conflict.Message };
            }
            catch (KeyNotFoundException notFound)
            {
                req.Http.Response.StatusCode = 404;
                return new ApiErrorResponse { Error = ApiResultEnum.NotFound, Message = notFound.Message };
            }
            catch (UnauthorizedAccessException denied)
            {
                return RouteAuthRefusal.Forbid(req, denied.Message);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException)
            {
                req.Http.Response.StatusCode = 400;
                return new ApiErrorResponse { Error = ApiResultEnum.BadRequest, Message = ex.Message };
            }
        }

        // Apply the querystring's memory filters on top of the request, trimmed. An unknown type is refused.
        private static bool TryApplyMemoryFilters(ApiRequest req, MemoryEnumerateRequest request, out string? error)
        {
            error = null;
            string? vesselId = Trimmed(QueryValueReader.Read(req, "vesselId"));
            if (vesselId != null) request.VesselId = vesselId;

            string? search = Trimmed(QueryValueReader.Read(req, "search"));
            if (search != null) request.Search = search;

            string? topic = Trimmed(QueryValueReader.Read(req, "topic"));
            if (topic != null) request.Topic = topic;

            string? typeText = Trimmed(QueryValueReader.Read(req, "type"));
            if (typeText != null)
            {
                if (!Enum.TryParse(typeText, true, out MemoryTypeEnum parsed))
                {
                    error = "Unknown memory type: " + typeText;
                    return false;
                }

                request.Type = parsed;
            }

            return true;
        }

        private static string? Trimmed(string? value)
        {
            return String.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        private static ApiErrorResponse AuthError(ApiRequest req)
        {
            return new ApiErrorResponse
            {
                Error = ApiResultEnum.BadRequest,
                Message = req.Http.Response.StatusCode == 401
                    ? "Authentication required"
                    : "You do not have permission to perform this action"
            };
        }

        private static async Task<AuthContext?> AuthorizeAsync(
            ApiRequest req,
            Func<HttpContextBase, Task<AuthContext>> authenticate,
            IAuthorizationService authz)
        {
            AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
            if (!authz.IsAuthorized(ctx, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery))
            {
                req.Http.Response.StatusCode = ctx.IsAuthenticated ? 403 : 401;
                return null;
            }

            return ctx;
        }
    }
}
