namespace Armada.Server.Mcp.Tools
{
    using System;
    using System.Text.Json;
    using System.Threading.Tasks;

    /// <summary>
    /// Registers MCP tools for querying process-local long-running jobs.
    /// </summary>
    public static class McpLongRunningJobTools
    {
        #region Private-Members

        private static readonly JsonSerializerOptions _JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        #endregion

        #region Public-Methods

        /// <summary>
        /// Register long-running job MCP tools.
        /// </summary>
        /// <param name="register">Tool registration delegate.</param>
        /// <param name="jobs">Shared process-local job service.</param>
        public static void Register(RegisterToolDelegate register, LongRunningJobService jobs)
        {
            if (register == null) throw new ArgumentNullException(nameof(register));
            if (jobs == null) throw new ArgumentNullException(nameof(jobs));

            register(
                "armada_job_status",
                "Get the current state of a long-running job. Successful jobs include their final result; failed jobs include a bounded error.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        jobId = new { type = "string", description = "Job ID returned by a long-running Armada tool (job_ prefix)" }
                    },
                    required = new[] { "jobId" }
                },
                (args) =>
                {
                    if (!args.HasValue)
                        return Task.FromResult<object>(new { Error = "missing args", Code = "missing_job_id" });

                    JobStatusArgs request = JsonSerializer.Deserialize<JobStatusArgs>(args.Value, _JsonOptions)!;
                    if (String.IsNullOrWhiteSpace(request.JobId))
                        return Task.FromResult<object>(new { Error = "jobId is required", Code = "missing_job_id" });

                    if (!jobs.TryGetStatus(request.JobId, out LongRunningJob? job) || job == null)
                    {
                        return Task.FromResult<object>(new
                        {
                            Error = "job not found",
                            Code = "job_not_found",
                            request.JobId
                        });
                    }

                    return Task.FromResult(ShapeStatus(job));
                });
        }

        #endregion

        #region Private-Methods

        private static object ShapeStatus(LongRunningJob job)
        {
            if (job.Status == LongRunningJobStatusEnum.Succeeded)
            {
                return new
                {
                    job.JobId,
                    job.Operation,
                    job.Status,
                    job.SubmittedAtUtc,
                    job.StartedAtUtc,
                    job.CompletedAtUtc,
                    job.Result
                };
            }

            if (job.Status == LongRunningJobStatusEnum.Failed)
            {
                return new
                {
                    job.JobId,
                    job.Operation,
                    job.Status,
                    job.SubmittedAtUtc,
                    job.StartedAtUtc,
                    job.CompletedAtUtc,
                    Error = job.FailureMessage
                };
            }

            return new
            {
                job.JobId,
                job.Operation,
                job.Status,
                job.SubmittedAtUtc,
                job.StartedAtUtc
            };
        }

        private sealed class JobStatusArgs
        {
            /// <summary>
            /// Job identifier to query.
            /// </summary>
            public string JobId { get; set; } = String.Empty;
        }

        #endregion
    }
}
