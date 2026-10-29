namespace Armada.Server.Routes
{
    using System;
    using System.Globalization;
    using System.Text.Json;
    using System.Threading.Tasks;
    using WatsonWebserver;
    using WatsonWebserver.Core;
    using WatsonWebserver.Core.OpenApi;
    using Armada.Core;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;

    /// <summary>
    /// REST routes for Evaluate (probe-only Quick model probes of a captain or a model endpoint) and the runs it records.
    /// Every operation enforces the same rules as MCP through <see cref="ModelProbeAccessService"/>.
    /// </summary>
    public class ModelProbeRoutes
    {
        #region Private-Members

        private readonly ModelProbeAccessService _Service;
        private readonly JsonSerializerOptions _JsonOptions;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="service">Caller-scoped model probe service.</param>
        /// <param name="jsonOptions">Serializer options for request bodies.</param>
        /// <exception cref="ArgumentNullException">Thrown when an argument is null.</exception>
        public ModelProbeRoutes(ModelProbeAccessService service, JsonSerializerOptions jsonOptions)
        {
            _Service = service ?? throw new ArgumentNullException(nameof(service));
            _JsonOptions = jsonOptions ?? throw new ArgumentNullException(nameof(jsonOptions));
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Register routes with the application.
        /// </summary>
        /// <param name="app">Web server.</param>
        /// <param name="authenticate">Authenticator.</param>
        /// <param name="authz">Route authorization.</param>
        public void Register(Webserver app, Func<HttpContextBase, Task<AuthContext>> authenticate, IAuthorizationService authz)
        {
            app.Post("/api/v1/captains/{id}/evaluate", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                ApiErrorResponse? denied = Deny(req, ctx, authz);
                if (denied != null) return denied;
                try
                {
                    EvaluateCaptainRequest body = ReadBody<EvaluateCaptainRequest>(req);
                    ModelProbeRun run = await _Service.EvaluateCaptainAsync(ctx, req.Parameters["id"], body.Depth).ConfigureAwait(false);
                    req.Http.Response.StatusCode = 201;
                    return run;
                }
                catch (Exception ex) when (RouteErrorMapper.IsMapped(ex))
                {
                    return RouteErrorMapper.ToResponse(req, ex);
                }
            },
            api => api
                .WithTag("ModelProbes")
                .WithSummary("Evaluate a captain")
                .WithDescription("Starts a Quick Evaluate: probe-only mechanical probes (output contract, served context, ASCII and format rules, tool calling, speed, determinism) run through the captain's own runtime as Harbor jobs. Returns the queued run at once; poll GET /api/v1/model-probe-runs/{id}. Each probe records an observation; a probe a runtime cannot run is CouldNotRun with the reason. Evaluation runs never count in stage outcomes and change no routing or captain settings. Global admins and tenant admins of the captain's tenant only (403 otherwise); depth Full is 400 (not implemented until Phase 5).")
                .WithParameter(OpenApiParameterMetadata.Path("id", "Captain ID (cpt_ prefix)"))
                .WithRequestBody(OpenApiJson.BodyFor<EvaluateCaptainRequest>("Depth (Quick, the default; Full is not implemented until Phase 5)", false))
                .WithResponse(201, OpenApiJson.For<ModelProbeRun>("The queued run"))
                .WithResponse(400, OpenApiResponseMetadata.BadRequest())
                .WithResponse(403, OpenApiJson.For<ApiErrorResponse>("Not an admin"))
                .WithResponse(404, OpenApiResponseMetadata.NotFound())
                .WithSecurity("ApiKey"));

            app.Post("/api/v1/model-endpoints/{id}/evaluate", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                ApiErrorResponse? denied = Deny(req, ctx, authz);
                if (denied != null) return denied;
                try
                {
                    EvaluateCaptainRequest body = ReadBody<EvaluateCaptainRequest>(req);
                    ModelProbeRun run = await _Service.EvaluateEndpointAsync(ctx, req.Parameters["id"], body.Depth).ConfigureAwait(false);
                    req.Http.Response.StatusCode = 201;
                    return run;
                }
                catch (Exception ex) when (RouteErrorMapper.IsMapped(ex))
                {
                    return RouteErrorMapper.ToResponse(req, ex);
                }
            },
            api => api
                .WithTag("ModelProbes")
                .WithSummary("Evaluate a model endpoint")
                .WithDescription("Starts a Quick Evaluate of a model endpoint through the API endpoint runtime (a transient captain that is never saved). Returns the queued run at once. Any caller who can read the endpoint (GET /api/v1/model-endpoints/{id}); 404 otherwise. Depth Full is 400 (not implemented until Phase 5).")
                .WithParameter(OpenApiParameterMetadata.Path("id", "Model endpoint ID (mep_ prefix)"))
                .WithRequestBody(OpenApiJson.BodyFor<EvaluateCaptainRequest>("Depth (Quick, the default; Full is not implemented until Phase 5)", false))
                .WithResponse(201, OpenApiJson.For<ModelProbeRun>("The queued run"))
                .WithResponse(400, OpenApiResponseMetadata.BadRequest())
                .WithResponse(404, OpenApiResponseMetadata.NotFound())
                .WithSecurity("ApiKey"));

            app.Get("/api/v1/model-probe-runs", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                ApiErrorResponse? denied = Deny(req, ctx, authz);
                if (denied != null) return denied;
                try
                {
                    return await _Service.ListRunsAsync(ctx, ParseQuery(key => QueryValueReader.Read(req.Query, key))).ConfigureAwait(false);
                }
                catch (Exception ex) when (RouteErrorMapper.IsMapped(ex))
                {
                    return RouteErrorMapper.ToResponse(req, ex);
                }
            },
            api => api
                .WithTag("ModelProbes")
                .WithSummary("List Evaluate runs")
                .WithDescription("One page of Evaluate runs, newest first, without their per-probe results. A global admin sees every tenant; everyone else sees their own tenant.")
                .WithParameter(OpenApiParameterMetadata.Query("tenantId", "Tenant filter (global admins only; ignored otherwise)", false))
                .WithParameter(OpenApiParameterMetadata.Query("captainId", "Captain filter (cpt_ prefix)", false))
                .WithParameter(OpenApiParameterMetadata.Query("modelEndpointId", "Model endpoint filter (mep_ prefix)", false))
                .WithParameter(OpenApiParameterMetadata.Query("status", "Queued, Running, Completed, or Failed", false))
                .WithParameter(OpenApiParameterMetadata.Query("pageNumber", "Page number (default 1)", false, OpenApiSchemaMetadata.Integer()))
                .WithParameter(OpenApiParameterMetadata.Query("pageSize", "Page size (1-1000, default 100)", false, OpenApiSchemaMetadata.Integer()))
                .WithResponse(200, OpenApiJson.For<EnumerationResult<ModelProbeRun>>("One page"))
                .WithResponse(400, OpenApiResponseMetadata.BadRequest())
                .WithSecurity("ApiKey"));

            app.Post("/api/v1/model-probe-runs/enumerate", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                ApiErrorResponse? denied = Deny(req, ctx, authz);
                if (denied != null) return denied;
                try
                {
                    return await _Service.ListRunsAsync(ctx, ReadBody<ModelProbeRunQuery>(req)).ConfigureAwait(false);
                }
                catch (Exception ex) when (RouteErrorMapper.IsMapped(ex))
                {
                    return RouteErrorMapper.ToResponse(req, ex);
                }
            },
            api => api
                .WithTag("ModelProbes")
                .WithSummary("Enumerate model probe runs")
                .WithDescription("One page of quick model probe runs visible to the caller, newest first. The JSON body (ModelProbeRunQuery) carries TenantId (global admins only), CaptainId, ModelEndpointId, Status, PageNumber, and PageSize (1-1000, default 100).")
                .WithRequestBody(OpenApiJson.BodyFor<ModelProbeRunQuery>("Paging and filters", false))
                .WithResponse(200, OpenApiJson.For<EnumerationResult<ModelProbeRun>>("One page"))
                .WithResponse(400, OpenApiResponseMetadata.BadRequest())
                .WithSecurity("ApiKey"));

            app.Get("/api/v1/model-probe-runs/{id}", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                ApiErrorResponse? denied = Deny(req, ctx, authz);
                if (denied != null) return denied;
                try
                {
                    return await _Service.GetRunAsync(ctx, req.Parameters["id"]).ConfigureAwait(false);
                }
                catch (Exception ex) when (RouteErrorMapper.IsMapped(ex))
                {
                    return RouteErrorMapper.ToResponse(req, ex);
                }
            },
            api => api
                .WithTag("ModelProbes")
                .WithSummary("Get an Evaluate run")
                .WithDescription("The run with its per-probe results (one per probe; Pass, Fail or CouldNotRun with the reason). 404 when the run does not exist or belongs to another tenant.")
                .WithParameter(OpenApiParameterMetadata.Path("id", "Run ID"))
                .WithResponse(200, OpenApiJson.For<ModelProbeRun>("The run"))
                .WithResponse(404, OpenApiResponseMetadata.NotFound())
                .WithSecurity("ApiKey"));
        }

        /// <summary>
        /// Parse list filters from name/value pairs (querystring); shared with MCP.
        /// </summary>
        /// <param name="value">Reads one value by name, or null.</param>
        /// <returns>The query.</returns>
        /// <exception cref="ArgumentNullException">Thrown when value is null.</exception>
        /// <exception cref="ArgumentException">Thrown for an invalid status or page number.</exception>
        public static ModelProbeRunQuery ParseQuery(Func<string, string?> value)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            ModelProbeRunQuery query = new ModelProbeRunQuery
            {
                TenantId = Blank(value("tenantId")),
                CaptainId = Blank(value("captainId")),
                ModelEndpointId = Blank(value("modelEndpointId"))
            };

            string? status = Blank(value("status"));
            if (status != null)
            {
                if (!EnumNames.TryParse(status, true, out ModelProbeRunStatusEnum parsed)) throw new ArgumentException("Invalid status: " + status);
                query.Status = parsed;
            }

            int? pageNumber = ParseInt(value("pageNumber"), "pageNumber");
            if (pageNumber.HasValue) query.PageNumber = pageNumber.Value;
            int? pageSize = ParseInt(value("pageSize"), "pageSize");
            if (pageSize.HasValue) query.PageSize = pageSize.Value;
            return query;
        }

        #endregion

        #region Private-Methods

        private T ReadBody<T>(ApiRequest req) where T : class, new()
        {
            string body = req.Http.Request.DataAsString ?? String.Empty;
            if (String.IsNullOrWhiteSpace(body)) return new T();
            try
            {
                return JsonSerializer.Deserialize<T>(body, _JsonOptions) ?? new T();
            }
            catch (JsonException ex)
            {
                throw new ArgumentException("Invalid request body: " + ex.Message, ex);
            }
        }

        private static ApiErrorResponse? Deny(ApiRequest req, AuthContext ctx, IAuthorizationService authz)
        {
            if (authz.IsAuthorized(ctx, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery)) return null;
            req.Http.Response.StatusCode = ctx.IsAuthenticated ? 403 : 401;
            return new ApiErrorResponse
            {
                Error = ctx.IsAuthenticated ? ApiResultEnum.Forbidden : ApiResultEnum.NotAuthorized,
                Message = ctx.IsAuthenticated ? "You do not have permission to perform this action" : "Authentication required"
            };
        }

        private static int? ParseInt(string? value, string name)
        {
            string? text = Blank(value);
            if (text == null) return null;
            if (!Int32.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)) throw new ArgumentException("Invalid " + name + ": " + text);
            return parsed;
        }

        private static string? Blank(string? value)
        {
            return String.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        #endregion
    }
}
