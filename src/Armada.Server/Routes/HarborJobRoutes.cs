namespace Armada.Server.Routes
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Threading.Tasks;
    using WatsonWebserver;
    using WatsonWebserver.Core;
    using Armada.Core.Harbor;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;

    /// <summary>
    /// Operator routes to list, inspect and stop Harbor runner jobs. Registered only when Harbor is enabled. Every
    /// route applies the shared runner authorization rule through <see cref="HarborJobService"/>; a job the caller may
    /// not see reads as not found.
    /// </summary>
    public sealed class HarborJobRoutes
    {
        #region Private-Members

        private readonly HarborJobService _Jobs;
        private readonly JsonSerializerOptions _JsonOptions;

        #endregion

        #region Constructors-and-Factories

        /// <summary>Instantiate the Harbor job routes.</summary>
        /// <param name="jobs">Harbor job service.</param>
        /// <param name="jsonOptions">Application JSON options.</param>
        public HarborJobRoutes(HarborJobService jobs, JsonSerializerOptions jsonOptions)
        {
            _Jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));
            _JsonOptions = jsonOptions ?? throw new ArgumentNullException(nameof(jsonOptions));
        }

        #endregion

        #region Public-Methods

        /// <summary>Register the job routes.</summary>
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

            app.Get("/api/v1/harbor-runners/jobs", async (ApiRequest req) =>
            {
                AuthContext context = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(context, "GET", "/api/v1/harbor-runners/jobs")) return Denied(req, context);
                string? runnerId = req.Query.GetValueOrDefault("runnerId");
                bool activeOnly = Boolean.TryParse(req.Query.GetValueOrDefault("activeOnly"), out bool parsedActive) && parsedActive;
                int limit = Int32.TryParse(req.Query.GetValueOrDefault("limit"), out int parsedLimit) ? parsedLimit : 100;
                List<HarborJobRecord> jobs = await _Jobs.ListAsync(context, runnerId, activeOnly, limit, req.Http.Token).ConfigureAwait(false);
                return (object)new HarborJobListResponse { Jobs = jobs };
            });

            app.Get("/api/v1/harbor-runners/jobs/{jobId}", async (ApiRequest req) =>
            {
                AuthContext context = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(context, "GET", "/api/v1/harbor-runners/jobs")) return Denied(req, context);
                HarborJobRecord? job = await _Jobs.GetAsync(context, req.Parameters["jobId"] ?? String.Empty, req.Http.Token).ConfigureAwait(false);
                if (job == null)
                {
                    req.Http.Response.StatusCode = 404;
                    return (object)new ApiErrorResponse { Error = ApiResultEnum.NotFound, Message = "harbor_job_unknown" };
                }
                return (object)job;
            });

            app.Post("/api/v1/harbor-runners/jobs/{jobId}/stop", async (ApiRequest req) =>
            {
                AuthContext context = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(context, "POST", "/api/v1/harbor-runners/jobs/stop")) return Denied(req, context);
                HarborCommandResult result = await _Jobs.StopAsync(context, req.Parameters["jobId"] ?? String.Empty, req.Http.Token).ConfigureAwait(false);
                if (result.Accepted) return (object)new HarborJobStopResponse { Stopped = true, Reason = result.Reason };

                req.Http.Response.StatusCode = result.Reason switch
                {
                    "harbor_job_unknown" => 404,
                    "harbor_command_unauthorized" => 403,
                    _ => 409
                };
                return (object)new ApiErrorResponse
                {
                    Error = req.Http.Response.StatusCode == 404 ? ApiResultEnum.NotFound : ApiResultEnum.Conflict,
                    Message = result.Reason
                };
            });
        }

        #endregion

        #region Private-Methods

        private static object Denied(ApiRequest request, AuthContext context)
        {
            bool authenticated = context != null && context.IsAuthenticated;
            request.Http.Response.StatusCode = authenticated ? 403 : 401;
            return new ApiErrorResponse
            {
                Error = ApiResultEnum.BadRequest,
                Message = authenticated ? "Administrator permission required" : "Authentication required"
            };
        }

        #endregion
    }

    /// <summary>Harbor jobs visible to the caller.</summary>
    public sealed class HarborJobListResponse
    {
        /// <summary>Jobs, newest first.</summary>
        public List<HarborJobRecord> Jobs { get; set; } = new List<HarborJobRecord>();
    }

    /// <summary>Result of an accepted Harbor job stop.</summary>
    public sealed class HarborJobStopResponse
    {
        /// <summary>Whether the stop was accepted.</summary>
        public bool Stopped { get; set; }

        /// <summary>Release reason when the runner was disconnected and the job was released; empty otherwise.</summary>
        public string Reason { get; set; } = String.Empty;
    }
}
