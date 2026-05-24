namespace Armada.Server.Routes
{
    using System;
    using System.Text.Json;
    using WatsonWebserver;
    using WatsonWebserver.Core;
    using WatsonWebserver.Core.OpenApi;
    using Armada.Server;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;

    /// <summary>
    /// REST API routes for code index and graph query operations.
    /// All routes are vessel-scoped under /api/v1/vessels/{vesselId}/code-index/...
    /// </summary>
    public class CodeIndexRoutes
    {
        #region Private-Members

        private readonly ICodeIndexService _codeIndex;
        private readonly DatabaseDriver _database;
        private readonly JsonSerializerOptions _jsonOptions;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="codeIndex">Code index service.</param>
        /// <param name="database">Database driver.</param>
        /// <param name="jsonOptions">JSON serializer options.</param>
        public CodeIndexRoutes(ICodeIndexService codeIndex, DatabaseDriver database, JsonSerializerOptions jsonOptions)
        {
            _codeIndex = codeIndex ?? throw new ArgumentNullException(nameof(codeIndex));
            _database = database ?? throw new ArgumentNullException(nameof(database));
            _jsonOptions = jsonOptions ?? throw new ArgumentNullException(nameof(jsonOptions));
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
            Func<WatsonWebserver.Core.HttpContextBase, Task<AuthContext>> authenticate,
            IAuthorizationService authz)
        {
            app.Get("/api/v1/vessels/{vesselId}/code-index/status", async (ApiRequest req) =>
            {
                string vesselId = ReadRouteVesselId(req);
                AuthContext? ctx = await AuthorizeVesselAccessAsync(req, authenticate, authz, vesselId).ConfigureAwait(false);
                if (ctx == null) return BuildAuthError(req);

                return await _codeIndex.GetStatusAsync(vesselId).ConfigureAwait(false);
            },
            api => api
                .WithTag("Code Index")
                .WithSummary("Get code index status")
                .WithDescription("Return persisted index status for a vessel, including freshness, indexed commit, current commit, document count, and chunk count.")
                .WithParameter(OpenApiParameterMetadata.Path("vesselId", "Vessel ID (vsl_ prefix)"))
                .WithResponse(200, OpenApiJson.For<CodeIndexStatus>("Code index status"))
                .WithSecurity("ApiKey"));

            app.Post("/api/v1/vessels/{vesselId}/code-index/update", async (ApiRequest req) =>
            {
                string vesselId = ReadRouteVesselId(req);
                AuthContext? ctx = await AuthorizeVesselAccessAsync(req, authenticate, authz, vesselId).ConfigureAwait(false);
                if (ctx == null) return BuildAuthError(req);

                return await _codeIndex.UpdateAsync(vesselId).ConfigureAwait(false);
            },
            api => api
                .WithTag("Code Index")
                .WithSummary("Refresh code index")
                .WithDescription("Refresh the Admiral-owned code index for a vessel's default branch and rewrite chunks plus graph sidecars.")
                .WithParameter(OpenApiParameterMetadata.Path("vesselId", "Vessel ID (vsl_ prefix)"))
                .WithResponse(200, OpenApiJson.For<CodeIndexStatus>("Updated code index status"))
                .WithSecurity("ApiKey"));

            app.Post<CodeSearchRequest>("/api/v1/vessels/{vesselId}/code-index/search", async (ApiRequest req) =>
            {
                string vesselId = ReadRouteVesselId(req);
                AuthContext? ctx = await AuthorizeVesselAccessAsync(req, authenticate, authz, vesselId).ConfigureAwait(false);
                if (ctx == null) return BuildAuthError(req);

                CodeSearchRequest searchRequest = JsonSerializer.Deserialize<CodeSearchRequest>(req.Http.Request.DataAsString, _jsonOptions)
                    ?? new CodeSearchRequest();
                searchRequest.VesselId = vesselId;

                if (String.IsNullOrWhiteSpace(searchRequest.Query))
                {
                    req.Http.Response.StatusCode = 400;
                    return new ApiErrorResponse { Error = ApiResultEnum.BadRequest, Message = "query is required" };
                }

                return await _codeIndex.SearchAsync(searchRequest).ConfigureAwait(false);
            },
            api => api
                .WithTag("Code Index")
                .WithSummary("Search code index")
                .WithDescription("Search indexed code chunks for a vessel using lexical, semantic, signature, and graph-aware ranking signals according to server settings.")
                .WithParameter(OpenApiParameterMetadata.Path("vesselId", "Vessel ID (vsl_ prefix)"))
                .WithRequestBody(OpenApiJson.BodyFor<CodeSearchRequest>("Code search request", true))
                .WithResponse(200, OpenApiJson.For<CodeSearchResponse>("Code search results"))
                .WithSecurity("ApiKey"));

            app.Post<CodeGraphSymbolSearchRequest>("/api/v1/vessels/{vesselId}/code-index/search-symbols", async (ApiRequest req) =>
            {
                string vesselId = req.Parameters["vesselId"];
                AuthContext? ctx = await AuthorizeVesselAccessAsync(req, authenticate, authz, vesselId).ConfigureAwait(false);
                if (ctx == null) return BuildAuthError(req);

                CodeGraphSymbolSearchRequest request = JsonSerializer.Deserialize<CodeGraphSymbolSearchRequest>(req.Http.Request.DataAsString, _jsonOptions)
                    ?? new CodeGraphSymbolSearchRequest();
                request.VesselId = vesselId;

                if (String.IsNullOrWhiteSpace(request.Query))
                {
                    req.Http.Response.StatusCode = 400;
                    return new ApiErrorResponse { Error = ApiResultEnum.BadRequest, Message = "query is required" };
                }

                return await _codeIndex.SearchSymbolsAsync(request).ConfigureAwait(false);
            },
            api => api
                .WithTag("Code Index")
                .WithSummary("Search graph symbols")
                .WithDescription("Search symbols in a vessel's code graph sidecars. Returns ranked matches with kind, path, and qualified name. Sidecar freshness warnings are included. Vectors are never returned.")
                .WithParameter(OpenApiParameterMetadata.Path("vesselId", "Vessel ID (vsl_ prefix)"))
                .WithRequestBody(OpenApiJson.BodyFor<CodeGraphSymbolSearchRequest>("Symbol search request", true))
                .WithResponse(200, OpenApiJson.For<CodeGraphSymbolSearchResponse>("Symbol search results"))
                .WithSecurity("ApiKey"));

            app.Post<CodeGraphNeighborsRequest>("/api/v1/vessels/{vesselId}/code-index/callers", async (ApiRequest req) =>
            {
                string vesselId = req.Parameters["vesselId"];
                AuthContext? ctx = await AuthorizeVesselAccessAsync(req, authenticate, authz, vesselId).ConfigureAwait(false);
                if (ctx == null) return BuildAuthError(req);

                CodeGraphNeighborsRequest request = JsonSerializer.Deserialize<CodeGraphNeighborsRequest>(req.Http.Request.DataAsString, _jsonOptions)
                    ?? new CodeGraphNeighborsRequest();
                request.VesselId = vesselId;

                if (String.IsNullOrWhiteSpace(request.Symbol))
                {
                    req.Http.Response.StatusCode = 400;
                    return new ApiErrorResponse { Error = ApiResultEnum.BadRequest, Message = "symbol is required" };
                }

                return await _codeIndex.GetCallersAsync(request).ConfigureAwait(false);
            },
            api => api
                .WithTag("Code Index")
                .WithSummary("Get symbol callers")
                .WithDescription("Resolve direct callers of a symbol from a vessel's code graph sidecars. Returns ranked neighbor results with edge kind and source path. Sidecar freshness warnings are included.")
                .WithParameter(OpenApiParameterMetadata.Path("vesselId", "Vessel ID (vsl_ prefix)"))
                .WithRequestBody(OpenApiJson.BodyFor<CodeGraphNeighborsRequest>("Callers request", true))
                .WithResponse(200, OpenApiJson.For<CodeGraphNeighborsResponse>("Caller results"))
                .WithSecurity("ApiKey"));

            app.Post<CodeGraphNeighborsRequest>("/api/v1/vessels/{vesselId}/code-index/callees", async (ApiRequest req) =>
            {
                string vesselId = req.Parameters["vesselId"];
                AuthContext? ctx = await AuthorizeVesselAccessAsync(req, authenticate, authz, vesselId).ConfigureAwait(false);
                if (ctx == null) return BuildAuthError(req);

                CodeGraphNeighborsRequest request = JsonSerializer.Deserialize<CodeGraphNeighborsRequest>(req.Http.Request.DataAsString, _jsonOptions)
                    ?? new CodeGraphNeighborsRequest();
                request.VesselId = vesselId;

                if (String.IsNullOrWhiteSpace(request.Symbol))
                {
                    req.Http.Response.StatusCode = 400;
                    return new ApiErrorResponse { Error = ApiResultEnum.BadRequest, Message = "symbol is required" };
                }

                return await _codeIndex.GetCalleesAsync(request).ConfigureAwait(false);
            },
            api => api
                .WithTag("Code Index")
                .WithSummary("Get symbol callees")
                .WithDescription("Resolve direct callees of a symbol from a vessel's code graph sidecars. Returns ranked neighbor results with edge kind and source path. Sidecar freshness warnings are included.")
                .WithParameter(OpenApiParameterMetadata.Path("vesselId", "Vessel ID (vsl_ prefix)"))
                .WithRequestBody(OpenApiJson.BodyFor<CodeGraphNeighborsRequest>("Callees request", true))
                .WithResponse(200, OpenApiJson.For<CodeGraphNeighborsResponse>("Callee results"))
                .WithSecurity("ApiKey"));

            app.Post<CodeGraphImpactRequest>("/api/v1/vessels/{vesselId}/code-index/impact", async (ApiRequest req) =>
            {
                string vesselId = req.Parameters["vesselId"];
                AuthContext? ctx = await AuthorizeVesselAccessAsync(req, authenticate, authz, vesselId).ConfigureAwait(false);
                if (ctx == null) return BuildAuthError(req);

                CodeGraphImpactRequest request = JsonSerializer.Deserialize<CodeGraphImpactRequest>(req.Http.Request.DataAsString, _jsonOptions)
                    ?? new CodeGraphImpactRequest();
                request.VesselId = vesselId;

                if (String.IsNullOrWhiteSpace(request.Symbol))
                {
                    req.Http.Response.StatusCode = 400;
                    return new ApiErrorResponse { Error = ApiResultEnum.BadRequest, Message = "symbol is required" };
                }

                return await _codeIndex.GetImpactAsync(request).ConfigureAwait(false);
            },
            api => api
                .WithTag("Code Index")
                .WithSummary("Get symbol impact")
                .WithDescription("Traverse graph relationships from a seed symbol using bounded depth. Direction can be Callers, Callees, or Both. Returns impacted symbols with traversal depth and score. Sidecar freshness warnings are included.")
                .WithParameter(OpenApiParameterMetadata.Path("vesselId", "Vessel ID (vsl_ prefix)"))
                .WithRequestBody(OpenApiJson.BodyFor<CodeGraphImpactRequest>("Impact request", true))
                .WithResponse(200, OpenApiJson.For<CodeGraphImpactResponse>("Impact traversal results"))
                .WithSecurity("ApiKey"));

            app.Post<CodeGraphAffectedTestsRequest>("/api/v1/vessels/{vesselId}/code-index/affected-tests", async (ApiRequest req) =>
            {
                string vesselId = req.Parameters["vesselId"];
                AuthContext? ctx = await AuthorizeVesselAccessAsync(req, authenticate, authz, vesselId).ConfigureAwait(false);
                if (ctx == null) return BuildAuthError(req);

                CodeGraphAffectedTestsRequest request = JsonSerializer.Deserialize<CodeGraphAffectedTestsRequest>(req.Http.Request.DataAsString, _jsonOptions)
                    ?? new CodeGraphAffectedTestsRequest();
                request.VesselId = vesselId;

                if (String.IsNullOrWhiteSpace(request.Symbol))
                {
                    req.Http.Response.StatusCode = 400;
                    return new ApiErrorResponse { Error = ApiResultEnum.BadRequest, Message = "symbol is required" };
                }

                return await _codeIndex.SuggestAffectedTestsAsync(request).ConfigureAwait(false);
            },
            api => api
                .WithTag("Code Index")
                .WithSummary("Suggest affected tests")
                .WithDescription("Suggest test files likely affected by a symbol change using graph traversal and path-convention fallback. Returns ranked candidates with evidence depth and reasons. Sidecar freshness warnings are included.")
                .WithParameter(OpenApiParameterMetadata.Path("vesselId", "Vessel ID (vsl_ prefix)"))
                .WithRequestBody(OpenApiJson.BodyFor<CodeGraphAffectedTestsRequest>("Affected tests request", true))
                .WithResponse(200, OpenApiJson.For<CodeGraphAffectedTestsResponse>("Affected test candidates"))
                .WithSecurity("ApiKey"));

            app.Post<CodeGraphNodeRequest>("/api/v1/vessels/{vesselId}/code-index/node", async (ApiRequest req) =>
            {
                string vesselId = req.Parameters["vesselId"];
                AuthContext? ctx = await AuthorizeVesselAccessAsync(req, authenticate, authz, vesselId).ConfigureAwait(false);
                if (ctx == null) return BuildAuthError(req);

                CodeGraphNodeRequest request = JsonSerializer.Deserialize<CodeGraphNodeRequest>(req.Http.Request.DataAsString, _jsonOptions)
                    ?? new CodeGraphNodeRequest();
                request.VesselId = vesselId;

                if (String.IsNullOrWhiteSpace(request.Symbol))
                {
                    req.Http.Response.StatusCode = 400;
                    return new ApiErrorResponse { Error = ApiResultEnum.BadRequest, Message = "symbol is required" };
                }

                return await _codeIndex.GetNodeAsync(request).ConfigureAwait(false);
            },
            api => api
                .WithTag("Code Index")
                .WithSummary("Get graph node")
                .WithDescription("Resolve one graph symbol with direct callers, callees, and optional source excerpt. Sidecar freshness warnings are included.")
                .WithParameter(OpenApiParameterMetadata.Path("vesselId", "Vessel ID (vsl_ prefix)"))
                .WithRequestBody(OpenApiJson.BodyFor<CodeGraphNodeRequest>("Graph node request", true))
                .WithResponse(200, OpenApiJson.For<CodeGraphNodeResponse>("Graph node detail"))
                .WithSecurity("ApiKey"));

            app.Post<CodeGraphFileStructureRequest>("/api/v1/vessels/{vesselId}/code-index/files", async (ApiRequest req) =>
            {
                string vesselId = req.Parameters["vesselId"];
                AuthContext? ctx = await AuthorizeVesselAccessAsync(req, authenticate, authz, vesselId).ConfigureAwait(false);
                if (ctx == null) return BuildAuthError(req);

                CodeGraphFileStructureRequest request = JsonSerializer.Deserialize<CodeGraphFileStructureRequest>(req.Http.Request.DataAsString, _jsonOptions)
                    ?? new CodeGraphFileStructureRequest();
                request.VesselId = vesselId;

                return await _codeIndex.GetFileStructureAsync(request).ConfigureAwait(false);
            },
            api => api
                .WithTag("Code Index")
                .WithSummary("Get indexed file structure")
                .WithDescription("Return indexed file structure from graph sidecars, optionally including symbols per file.")
                .WithParameter(OpenApiParameterMetadata.Path("vesselId", "Vessel ID (vsl_ prefix)"))
                .WithRequestBody(OpenApiJson.BodyFor<CodeGraphFileStructureRequest>("File structure request", true))
                .WithResponse(200, OpenApiJson.For<CodeGraphFileStructureResponse>("Indexed file structure"))
                .WithSecurity("ApiKey"));

            app.Post<CodeGraphExploreRequest>("/api/v1/vessels/{vesselId}/code-index/explore", async (ApiRequest req) =>
            {
                string vesselId = req.Parameters["vesselId"];
                AuthContext? ctx = await AuthorizeVesselAccessAsync(req, authenticate, authz, vesselId).ConfigureAwait(false);
                if (ctx == null) return BuildAuthError(req);

                CodeGraphExploreRequest request = JsonSerializer.Deserialize<CodeGraphExploreRequest>(req.Http.Request.DataAsString, _jsonOptions)
                    ?? new CodeGraphExploreRequest();
                request.VesselId = vesselId;

                if (String.IsNullOrWhiteSpace(request.Query))
                {
                    req.Http.Response.StatusCode = 400;
                    return new ApiErrorResponse { Error = ApiResultEnum.BadRequest, Message = "query is required" };
                }

                return await _codeIndex.ExploreAsync(request).ConfigureAwait(false);
            },
            api => api
                .WithTag("Code Index")
                .WithSummary("Explore graph context")
                .WithDescription("Explore graph relationships and source excerpts around a symbol query. Returns grouped files and relationship edges.")
                .WithParameter(OpenApiParameterMetadata.Path("vesselId", "Vessel ID (vsl_ prefix)"))
                .WithRequestBody(OpenApiJson.BodyFor<CodeGraphExploreRequest>("Graph explore request", true))
                .WithResponse(200, OpenApiJson.For<CodeGraphExploreResponse>("Graph exploration results"))
                .WithSecurity("ApiKey"));
        }

        #endregion

        #region Private-Methods

        private async Task<AuthContext?> AuthorizeVesselAccessAsync(
            ApiRequest req,
            Func<WatsonWebserver.Core.HttpContextBase, Task<AuthContext>> authenticate,
            IAuthorizationService authz,
            string vesselId)
        {
            AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
            if (!authz.IsAuthorized(ctx, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery))
            {
                req.Http.Response.StatusCode = ctx.IsAuthenticated ? 403 : 401;
                return null;
            }

            Vessel? vessel = await ReadVesselForContextAsync(ctx, vesselId).ConfigureAwait(false);
            if (vessel == null)
            {
                req.Http.Response.StatusCode = 404;
                return null;
            }

            return ctx;
        }

        private async Task<Vessel?> ReadVesselForContextAsync(AuthContext ctx, string vesselId)
        {
            if (ctx.IsAdmin) return await _database.Vessels.ReadAsync(vesselId).ConfigureAwait(false);
            if (ctx.IsTenantAdmin) return await _database.Vessels.ReadAsync(ctx.TenantId!, vesselId).ConfigureAwait(false);
            return await _database.Vessels.ReadAsync(ctx.TenantId!, ctx.UserId!, vesselId).ConfigureAwait(false);
        }

        private static string ReadRouteVesselId(ApiRequest req)
        {
            return req.Parameters.GetValueOrDefault("vesselId") ?? "";
        }

        private static ApiErrorResponse BuildAuthError(ApiRequest req)
        {
            string message = req.Http.Response.StatusCode == 401
                ? "Authentication required"
                : req.Http.Response.StatusCode == 404
                    ? "Vessel not found"
                    : "You do not have permission to perform this action";

            return new ApiErrorResponse
            {
                Error = ApiResultEnum.BadRequest,
                Message = message
            };
        }

        #endregion
    }
}
