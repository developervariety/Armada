namespace Armada.Server.Mcp.Tools
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Harbor;
    using Armada.Core.Models;
    using Armada.Core.Services;

    /// <summary>
    /// Registers the operator tools that list, inspect and stop Harbor jobs. Registered only while Harbor is enabled.
    /// Every tool applies the shared runner authorization rule through <see cref="HarborJobService"/>; stopping
    /// also needs a tenant or global administrator, the same level as the REST route.
    /// </summary>
    public static class McpHarborJobTools
    {
        #region Public-Members

        /// <summary>Tool that lists Harbor jobs.</summary>
        public const string ListToolName = "armada_harbor_jobs";

        /// <summary>Tool that reads one Harbor job.</summary>
        public const string GetToolName = "armada_harbor_job";

        /// <summary>Tool that stops one Harbor job.</summary>
        public const string StopToolName = "armada_harbor_job_stop";

        /// <summary>Refusal when a caller below tenant administrator stops a job.</summary>
        public const string AdministratorRequiredReason = "tenant_administrator_required";

        #endregion

        #region Private-Members

        private static readonly JsonSerializerOptions _JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        #endregion

        #region Public-Methods

        /// <summary>Register the Harbor job tools.</summary>
        /// <param name="register">Tool registration delegate.</param>
        /// <param name="jobs">Harbor job service.</param>
        public static void Register(RegisterToolDelegate register, HarborJobService jobs)
        {
            if (register == null) throw new ArgumentNullException(nameof(register));
            if (jobs == null) throw new ArgumentNullException(nameof(jobs));

            register(
                ListToolName,
                "List Harbor runner jobs the caller may see, newest first: the runner owner's jobs, or every job of an owner the caller administers. Each job carries its runner, enrollment and connection generation, state, last output sequence, mission and named failure reason.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        runnerId = new { type = "string", description = "Restrict to one runner" },
                        activeOnly = new { type = "boolean", description = "Only jobs that are not terminal (default false)" },
                        limit = new { type = "integer", description = "Maximum jobs, 1-1000 (default 100)" }
                    }
                },
                async (args) =>
                {
                    AuthContext caller = McpCallerContext.Require();
                    JobArgs parsed = Parse(args);
                    List<HarborJobRecord> list = await jobs.ListAsync(caller, parsed.RunnerId, parsed.ActiveOnly ?? false, parsed.Limit ?? 100, CancellationToken.None).ConfigureAwait(false);
                    return (object)new { Jobs = list };
                });

            register(
                GetToolName,
                "Read one Harbor runner job the caller may see. A job the caller may not see reads as unknown.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        jobId = new { type = "string", description = "Harbor job id (hjob_ prefix)" }
                    },
                    required = new[] { "jobId" }
                },
                async (args) =>
                {
                    AuthContext caller = McpCallerContext.Require();
                    JobArgs parsed = Parse(args);
                    HarborJobRecord? record = await jobs.GetAsync(caller, parsed.JobId ?? String.Empty, CancellationToken.None).ConfigureAwait(false);
                    if (record == null) return (object)new { Error = "Harbor job not found", Reason = "harbor_job_unknown" };
                    return (object)record;
                });

            register(
                StopToolName,
                "Stop a Harbor runner job. Needs a tenant or global administrator with authority over the runner owner. A job whose runner is disconnected is released and becomes lost with a named reason.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        jobId = new { type = "string", description = "Harbor job id (hjob_ prefix)" }
                    },
                    required = new[] { "jobId" }
                },
                async (args) =>
                {
                    AuthContext caller = McpCallerContext.Require();
                    if (!caller.IsAdmin && !caller.IsTenantAdmin)
                        return (object)new { Stopped = false, Reason = AdministratorRequiredReason };
                    JobArgs parsed = Parse(args);
                    HarborCommandResult result = await jobs.StopAsync(caller, parsed.JobId ?? String.Empty, CancellationToken.None).ConfigureAwait(false);
                    return (object)new { Stopped = result.Accepted, Reason = result.Reason };
                });
        }

        #endregion

        #region Private-Methods

        private static JobArgs Parse(JsonElement? args)
        {
            if (!args.HasValue) return new JobArgs();
            return JsonSerializer.Deserialize<JobArgs>(args.Value, _JsonOptions) ?? new JobArgs();
        }

        #endregion

        #region Private-Types

        private sealed class JobArgs
        {
            public string? JobId { get; set; } = null;

            public string? RunnerId { get; set; } = null;

            public bool? ActiveOnly { get; set; } = null;

            public int? Limit { get; set; } = null;
        }

        #endregion
    }
}
