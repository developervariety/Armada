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
    /// REST API routes for code graph query operations (symbol search, callers, callees, impact, affected tests).
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
