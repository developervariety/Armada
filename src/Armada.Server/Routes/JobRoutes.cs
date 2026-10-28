namespace Armada.Server.Routes
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.Json;
    using System.Threading.Tasks;
    using WatsonWebserver;
    using WatsonWebserver.Core;
    using WatsonWebserver.Core.OpenApi;
    using Armada.Server;
    using Armada.Core.Enums;
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

        private static readonly JsonSerializerOptions _BodyJsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

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
            app.Post("/api/v1/jobs/enumerate", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(ctx, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery))
                {
                    return RouteAuthRefusal.Refuse(req, ctx);
                }

                if (!EnumerationRequestReader.TryRead(req, _BodyJsonOptions, out EnumerationQuery query, out string? queryError))
                {
                    req.Http.Response.StatusCode = 400;
                    return new ApiErrorResponse { Error = ApiResultEnum.BadRequest, Message = queryError };
                }

                if (!TryFilter(_Jobs.ListJobs(out int unreadableRecords), query, out List<LongRunningJob> matching, out string? filterError))
                {
                    req.Http.Response.StatusCode = 400;
                    return new ApiErrorResponse { Error = ApiResultEnum.BadRequest, Message = filterError };
                }

                // The list is newest first with the job id as tie-break, so a page boundary never moves between requests.
                // A result can be large; a listed job carries status only and a single read returns it.
                EnumerationResult<LongRunningJob> page = EnumerationResult<LongRunningJob>.Page(query, matching);
                foreach (LongRunningJob job in page.Objects) job.Result = null;
                return (object)new JobEnumerationResult
                {
                    Success = page.Success,
                    PageNumber = page.PageNumber,
                    PageSize = page.PageSize,
                    TotalPages = page.TotalPages,
                    TotalRecords = page.TotalRecords,
                    Objects = page.Objects,
                    RequestedPageSize = page.RequestedPageSize,
                    MaxPageSize = page.MaxPageSize,
                    UnreadableJournalRecords = unreadableRecords
                };
            },
            api => api
                .WithTag("Jobs")
                .WithSummary("Enumerate background jobs")
                .WithDescription("Returns one page of the Admiral's long-running jobs, newest submitted first; TotalRecords counts every job that matches the filters. Paging and filters come from the JSON body (EnumerationQuery) or the querystring: pageNumber, pageSize (1 to 1000, default 10), order (CreatedAscending or CreatedDescending, by submission time), status (comma-separated: Accepted, Running, Succeeded, Failed, Lost), vesselId, createdAfter, createdBefore. List entries omit Result; UnreadableJournalRecords counts journal records that could not be read, whatever the page. Requires a global administrator.")
                .WithRequestBody(OpenApiJson.BodyFor<EnumerationQuery>("Paging and filters", false))
                .WithResponse(200, OpenApiJson.For<JobEnumerationResult>("One page of jobs"))
                .WithResponse(400, OpenApiResponseMetadata.BadRequest())
                .WithSecurity("ApiKey"));

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

        #region Private-Methods

        private static bool TryFilter(List<LongRunningJob> jobs, EnumerationQuery query, out List<LongRunningJob> matching, out string? error)
        {
            matching = new List<LongRunningJob>();
            error = null;

            HashSet<LongRunningJobStatusEnum>? statuses = null;
            if (!String.IsNullOrWhiteSpace(query.Status))
            {
                statuses = new HashSet<LongRunningJobStatusEnum>();
                foreach (string token in query.Status.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (!Enum.TryParse<LongRunningJobStatusEnum>(token, true, out LongRunningJobStatusEnum status))
                    {
                        error = "Unknown job status '" + token + "'. Use Accepted, Running, Succeeded, Failed or Lost.";
                        return false;
                    }
                    statuses.Add(status);
                }
            }

            IEnumerable<LongRunningJob> filtered = jobs;
            if (statuses != null) filtered = filtered.Where(job => statuses.Contains(job.Status));
            if (!String.IsNullOrWhiteSpace(query.VesselId)) filtered = filtered.Where(job => String.Equals(job.VesselId, query.VesselId, StringComparison.Ordinal));
            if (query.CreatedAfter.HasValue) filtered = filtered.Where(job => job.SubmittedAtUtc >= query.CreatedAfter.Value.ToUniversalTime());
            if (query.CreatedBefore.HasValue) filtered = filtered.Where(job => job.SubmittedAtUtc <= query.CreatedBefore.Value.ToUniversalTime());

            matching = filtered.ToList();
            if (query.Order == EnumerationOrderEnum.CreatedAscending) matching.Reverse();
            return true;
        }

        #endregion
    }
}
