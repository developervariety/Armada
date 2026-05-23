namespace Armada.Server.Routes
{
    using System;
    using System.Text.Json;
    using WatsonWebserver;
    using WatsonWebserver.Core;
    using WatsonWebserver.Core.OpenApi;
    using Armada.Server;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;

    /// <summary>
    /// REST API routes for code graph query operations (symbol search, callers, callees, impact, affected tests).
    /// </summary>
    public class CodeIndexRoutes
    {
        #region Private-Members

        private readonly ICodeIndexService _codeIndex;
        private readonly JsonSerializerOptions _jsonOptions;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="codeIndex">Code index service.</param>
        /// <param name="jsonOptions">JSON serializer options.</param>
        public CodeIndexRoutes(ICodeIndexService codeIndex, JsonSerializerOptions jsonOptions)
        {
            _codeIndex = codeIndex ?? throw new ArgumentNullException(nameof(codeIndex));
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
            app.Post<CodeGraphSymbolSearchRequest>("/api/v1/code-index/search-symbols", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(ctx, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery))
                {
                    req.Http.Response.StatusCode = ctx.IsAuthenticated ? 403 : 401;
                    return new ApiErrorResponse
                    {
                        Error = ctx.IsAuthenticated ? ApiResultEnum.BadRequest : ApiResultEnum.BadRequest,
                        Message = ctx.IsAuthenticated ? "You do not have permission to perform this action" : "Authentication required"
                    };
                }

                CodeGraphSymbolSearchRequest request = JsonSerializer.Deserialize<CodeGraphSymbolSearchRequest>(req.Http.Request.DataAsString, _jsonOptions)
                    ?? new CodeGraphSymbolSearchRequest();

                if (String.IsNullOrWhiteSpace(request.VesselId))
                {
                    req.Http.Response.StatusCode = 400;
                    return new ApiErrorResponse { Error = ApiResultEnum.BadRequest, Message = "vesselId is required" };
                }

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
                .WithRequestBody(OpenApiJson.BodyFor<CodeGraphSymbolSearchRequest>("Symbol search request", true))
                .WithResponse(200, OpenApiJson.For<CodeGraphSymbolSearchResponse>("Symbol search results"))
                .WithSecurity("ApiKey"));

            app.Post<CodeGraphNeighborsRequest>("/api/v1/code-index/callers", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(ctx, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery))
                {
                    req.Http.Response.StatusCode = ctx.IsAuthenticated ? 403 : 401;
                    return new ApiErrorResponse
                    {
                        Error = ctx.IsAuthenticated ? ApiResultEnum.BadRequest : ApiResultEnum.BadRequest,
                        Message = ctx.IsAuthenticated ? "You do not have permission to perform this action" : "Authentication required"
                    };
                }

                CodeGraphNeighborsRequest request = JsonSerializer.Deserialize<CodeGraphNeighborsRequest>(req.Http.Request.DataAsString, _jsonOptions)
                    ?? new CodeGraphNeighborsRequest();

                if (String.IsNullOrWhiteSpace(request.VesselId))
                {
                    req.Http.Response.StatusCode = 400;
                    return new ApiErrorResponse { Error = ApiResultEnum.BadRequest, Message = "vesselId is required" };
                }

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
                .WithRequestBody(OpenApiJson.BodyFor<CodeGraphNeighborsRequest>("Callers request", true))
                .WithResponse(200, OpenApiJson.For<CodeGraphNeighborsResponse>("Caller results"))
                .WithSecurity("ApiKey"));

            app.Post<CodeGraphNeighborsRequest>("/api/v1/code-index/callees", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(ctx, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery))
                {
                    req.Http.Response.StatusCode = ctx.IsAuthenticated ? 403 : 401;
                    return new ApiErrorResponse
                    {
                        Error = ctx.IsAuthenticated ? ApiResultEnum.BadRequest : ApiResultEnum.BadRequest,
                        Message = ctx.IsAuthenticated ? "You do not have permission to perform this action" : "Authentication required"
                    };
                }

                CodeGraphNeighborsRequest request = JsonSerializer.Deserialize<CodeGraphNeighborsRequest>(req.Http.Request.DataAsString, _jsonOptions)
                    ?? new CodeGraphNeighborsRequest();

                if (String.IsNullOrWhiteSpace(request.VesselId))
                {
                    req.Http.Response.StatusCode = 400;
                    return new ApiErrorResponse { Error = ApiResultEnum.BadRequest, Message = "vesselId is required" };
                }

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
                .WithRequestBody(OpenApiJson.BodyFor<CodeGraphNeighborsRequest>("Callees request", true))
                .WithResponse(200, OpenApiJson.For<CodeGraphNeighborsResponse>("Callee results"))
                .WithSecurity("ApiKey"));

            app.Post<CodeGraphImpactRequest>("/api/v1/code-index/impact", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(ctx, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery))
                {
                    req.Http.Response.StatusCode = ctx.IsAuthenticated ? 403 : 401;
                    return new ApiErrorResponse
                    {
                        Error = ctx.IsAuthenticated ? ApiResultEnum.BadRequest : ApiResultEnum.BadRequest,
                        Message = ctx.IsAuthenticated ? "You do not have permission to perform this action" : "Authentication required"
                    };
                }

                CodeGraphImpactRequest request = JsonSerializer.Deserialize<CodeGraphImpactRequest>(req.Http.Request.DataAsString, _jsonOptions)
                    ?? new CodeGraphImpactRequest();

                if (String.IsNullOrWhiteSpace(request.VesselId))
                {
                    req.Http.Response.StatusCode = 400;
                    return new ApiErrorResponse { Error = ApiResultEnum.BadRequest, Message = "vesselId is required" };
                }

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
                .WithRequestBody(OpenApiJson.BodyFor<CodeGraphImpactRequest>("Impact request", true))
                .WithResponse(200, OpenApiJson.For<CodeGraphImpactResponse>("Impact traversal results"))
                .WithSecurity("ApiKey"));

            app.Post<CodeGraphAffectedTestsRequest>("/api/v1/code-index/affected-tests", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(ctx, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery))
                {
                    req.Http.Response.StatusCode = ctx.IsAuthenticated ? 403 : 401;
                    return new ApiErrorResponse
                    {
                        Error = ctx.IsAuthenticated ? ApiResultEnum.BadRequest : ApiResultEnum.BadRequest,
                        Message = ctx.IsAuthenticated ? "You do not have permission to perform this action" : "Authentication required"
                    };
                }

                CodeGraphAffectedTestsRequest request = JsonSerializer.Deserialize<CodeGraphAffectedTestsRequest>(req.Http.Request.DataAsString, _jsonOptions)
                    ?? new CodeGraphAffectedTestsRequest();

                if (String.IsNullOrWhiteSpace(request.VesselId))
                {
                    req.Http.Response.StatusCode = 400;
                    return new ApiErrorResponse { Error = ApiResultEnum.BadRequest, Message = "vesselId is required" };
                }

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
                .WithRequestBody(OpenApiJson.BodyFor<CodeGraphAffectedTestsRequest>("Affected tests request", true))
                .WithResponse(200, OpenApiJson.For<CodeGraphAffectedTestsResponse>("Affected test candidates"))
                .WithSecurity("ApiKey"));
        }

        #endregion
    }
}
