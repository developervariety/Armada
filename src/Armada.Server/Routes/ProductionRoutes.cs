namespace Armada.Server.Routes
{
    using System;
    using WatsonWebserver;
    using WatsonWebserver.Core;
    using WatsonWebserver.Core.OpenApi;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;

    /// <summary>
    /// REST routes for verified production summaries.
    /// </summary>
    public sealed class ProductionRoutes
    {
        private readonly VerifiedProductionSummaryService _Production;

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="production">Verified production summary service.</param>
        public ProductionRoutes(VerifiedProductionSummaryService production)
        {
            _Production = production ?? throw new ArgumentNullException(nameof(production));
        }

        /// <summary>
        /// Register routes.
        /// </summary>
        /// <param name="app">Web server.</param>
        /// <param name="authenticate">Authentication delegate.</param>
        /// <param name="authz">Authorization service.</param>
        public void Register(
            Webserver app,
            Func<HttpContextBase, Task<AuthContext>> authenticate,
            IAuthorizationService authz)
        {
            app.Get("/api/v1/production/summary", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(ctx, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery))
                {
                    req.Http.Response.StatusCode = ctx.IsAuthenticated ? 403 : 401;
                    return (object)new ApiErrorResponse
                    {
                        Error = ApiResultEnum.BadRequest,
                        Message = ctx.IsAuthenticated
                            ? "You do not have permission to perform this action"
                            : "Authentication required"
                    };
                }

                ProductionSummaryQuery query = BuildQuery(req);
                return await _Production.SummarizeAsync(ctx, query).ConfigureAwait(false);
            },
            api => api
                .WithTag("Production")
                .WithSummary("Summarize verified production")
                .WithDescription("Returns a tenant-scoped, evidence-based production baseline. It reports verified landed slices, dispatch delay, armed-to-start and execution timing, first-pass acceptance, rescue cost, closeout delay, post-land regressions, repeated research, eligible idle lane time, and explicit warnings for incomplete or unavailable evidence. Host-slot queue time stays unavailable until its start timestamp is durable.")
                .WithParameter(OpenApiParameterMetadata.Query("fromUtc", "UTC summary start timestamp", false))
                .WithParameter(OpenApiParameterMetadata.Query("toUtc", "UTC summary end timestamp", false))
                .WithParameter(OpenApiParameterMetadata.Query("sourceFamily", "Optional exact objective source-family filter", false))
                .WithParameter(OpenApiParameterMetadata.Query("workType", "Optional exact objective work-type filter", false))
                .WithResponse(200, OpenApiJson.For<ProductionSummaryResult>("Verified production summary"))
                .WithSecurity("ApiKey"));
        }

        private static ProductionSummaryQuery BuildQuery(ApiRequest req)
        {
            ProductionSummaryQuery query = new ProductionSummaryQuery();
            if (DateTime.TryParse(Decode(req.Query.GetValueOrDefault("fromUtc")), out DateTime fromUtc))
                query.FromUtc = fromUtc.ToUniversalTime();
            if (DateTime.TryParse(Decode(req.Query.GetValueOrDefault("toUtc")), out DateTime toUtc))
                query.ToUtc = toUtc.ToUniversalTime();

            query.SourceFamily = Normalize(req.Query.GetValueOrDefault("sourceFamily"));
            query.WorkType = Normalize(req.Query.GetValueOrDefault("workType"));
            return query;
        }

        private static string? Normalize(string? value)
        {
            value = Decode(value);
            return String.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        private static string? Decode(string? value) => value == null ? null : Uri.UnescapeDataString(value);
    }
}
