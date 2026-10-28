namespace Armada.Server.Routes
{
    using System;
    using System.Threading.Tasks;
    using WatsonWebserver;
    using WatsonWebserver.Core;
    using Armada.Core.Harbor;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;

    /// <summary>
    /// Authorized read of in-memory Harbor host metrics. Registered only when Harbor is enabled. A caller who may
    /// not see the runner receives the same not-found result as an unknown runner.
    /// </summary>
    public sealed class HarborHostMetricsRoutes
    {
        private readonly HarborHostMetricsService _Metrics;

        /// <summary>Create the host metrics routes.</summary>
        /// <param name="metrics">Authorized metrics read.</param>
        public HarborHostMetricsRoutes(HarborHostMetricsService metrics)
        {
            _Metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        }

        /// <summary>Register the host metrics read.</summary>
        /// <param name="app">Web server.</param>
        /// <param name="authenticate">Verified request authentication.</param>
        /// <param name="authz">Common authorization service.</param>
        public void Register(
            Webserver app,
            Func<HttpContextBase, Task<AuthContext>> authenticate,
            IAuthorizationService authz)
        {
            if (app == null) throw new ArgumentNullException(nameof(app));
            if (authenticate == null) throw new ArgumentNullException(nameof(authenticate));
            if (authz == null) throw new ArgumentNullException(nameof(authz));

            app.Get("/api/v1/harbor-runners/{runnerId}/metrics", async (ApiRequest req) =>
            {
                AuthContext context = await authenticate(req.Http).ConfigureAwait(false);
                string runnerId = req.Parameters["runnerId"] ?? String.Empty;
                if (!authz.IsAuthorized(context, "GET", "/api/v1/harbor-runners/" + runnerId + "/metrics"))
                    return Denied(req, context);
                if (String.IsNullOrWhiteSpace(runnerId) || runnerId.Length > 450)
                {
                    req.Http.Response.StatusCode = 400;
                    return (object)new ApiErrorResponse { Error = ApiResultEnum.BadRequest, Message = "runnerId is required" };
                }

                HarborHostMetricsSnapshot? snapshot = await _Metrics.ReadAsync(context, runnerId, req.Http.Token).ConfigureAwait(false);
                if (snapshot == null)
                {
                    req.Http.Response.StatusCode = 404;
                    return (object)new ApiErrorResponse { Error = ApiResultEnum.NotFound, Message = "harbor_host_unknown" };
                }
                return (object)snapshot;
            });
        }

        private static object Denied(ApiRequest request, AuthContext context)
        {
            return RouteAuthRefusal.Refuse(request, context, "Administrator permission required");
        }
    }
}
