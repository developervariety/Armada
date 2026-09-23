namespace Armada.Server.Routes
{
    using System;
    using System.Collections.Generic;
    using System.Threading.Tasks;
    using WatsonWebserver;
    using WatsonWebserver.Core;
    using WatsonWebserver.Core.OpenApi;
    using Armada.Server;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;

    /// <summary>
    /// REST API routes that read the Admiral's long-running background jobs: dispatch, code-index
    /// refresh, merge processing, disk lifecycle and the other operations that return an accepted job.
    /// A job carries no tenant or user, so both routes require a global administrator, as the
    /// armada_job_status tool does.
    /// </summary>
    public class JobRoutes
    {
        #region Private-Members

        private readonly LongRunningJobService _Jobs;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="jobs">The Admiral's long-running job service.</param>
        public JobRoutes(LongRunningJobService jobs)
        {
            _Jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));
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
            Func<HttpContextBase, Task<AuthContext>> authenticate,
            IAuthorizationService authz)
        {
            app.Get("/api/v1/jobs", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(ctx, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery))
                {
                    return RouteAuthRefusal.Refuse(req, ctx);
                }

                List<LongRunningJob> jobs = _Jobs.ListJobs(out int unreadableRecords);

                // A result can be large; the list carries status only and a single read returns it.
                foreach (LongRunningJob job in jobs) job.Result = null;
                return (object)new
                {
                    Success = true,
                    Objects = jobs,
                    TotalRecords = jobs.Count,
                    UnreadableJournalRecords = unreadableRecords
                };
            },
            api => api
                .WithTag("Jobs")
                .WithSummary("List background jobs")
                .WithDescription("Returns the Admiral's long-running jobs newest first: jobs held in memory and journalled jobs kept for 14 days. List entries omit Result; UnreadableJournalRecords counts journal records that could not be read. Requires a global administrator.")
                .WithSecurity("ApiKey"));

            app.Get("/api/v1/jobs/{id}", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(ctx, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery))
                {
                    return RouteAuthRefusal.Refuse(req, ctx);
                }

                string id = req.Parameters["id"];
                if (!_Jobs.TryGetStatus(id, out LongRunningJob? job) || job == null)
                {
                    req.Http.Response.StatusCode = 404;
                    return new ApiErrorResponse { Error = ApiResultEnum.NotFound, Message = "Job not found" };
                }
                return (object)job;
            },
            api => api
                .WithTag("Jobs")
                .WithSummary("Get a background job")
                .WithDescription("Returns one long-running job, including its Result when it succeeded and its FailureMessage when it failed or was lost. Requires a global administrator.")
                .WithResponse(404, OpenApiResponseMetadata.NotFound())
                .WithSecurity("ApiKey"));
        }

        #endregion
    }
}
