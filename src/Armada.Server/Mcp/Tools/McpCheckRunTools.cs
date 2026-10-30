namespace Armada.Server.Mcp.Tools
{
    using System;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;

    /// <summary>
    /// Registers MCP tools for structured check-run execution and inspection.
    /// </summary>
    public static class McpCheckRunTools
    {
        /// <summary>
        /// Trailing output lines returned with a bounded check-run view. A failure's cause is
        /// almost always in the tail, so this is what makes the common question answerable in one
        /// call instead of a fetch-and-parse round trip.
        /// </summary>
        private const int _DefaultOutputTailLines = 40;

        /// <summary>
        /// Longest time run_check and retry_check_run wait before they return an unfinished check, below common
        /// client tool timeouts.
        /// </summary>
        private static readonly TimeSpan _DefaultCheckWait = TimeSpan.FromMinutes(2);

        private static readonly JsonSerializerOptions _JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter() }
        };

        /// <summary>
        /// Registers structured check-run MCP tools.
        /// </summary>
        /// <param name="register">Tool registration delegate.</param>
        /// <param name="database">Database driver.</param>
        /// <param name="checkRunService">Check run service.</param>
        /// <param name="checkWait">Longest time run_check and retry_check_run wait for a result. Defaults to two minutes.</param>
        public static void Register(RegisterToolDelegate register, DatabaseDriver database, CheckRunService checkRunService, TimeSpan? checkWait = null)
        {
            TimeSpan wait = checkWait ?? _DefaultCheckWait;
            register(
                "get_check_run",
                "Inspect one structured check run: status, exit code, parsed test and coverage summaries, artifacts, and the tail of the command output. Pass includeOutput=true for the complete log, which for a build or test run is routinely megabytes and can exceed the tool output limit.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        checkRunId = new { type = "string", description = "Check run ID (chk_ prefix)" },
                        includeOutput = new { type = "boolean", description = "Return the complete command log instead of a bounded tail. Defaults to false." },
                        outputTailLines = new { type = "integer", description = "How many trailing output lines to include when includeOutput is false. Defaults to 40." }
                    },
                    required = new[] { "checkRunId" }
                },
                async (args) =>
                {
                    CheckRunIdArgs request;
                    try
                    {
                        request = DeserializeArgs<CheckRunIdArgs>(args, "get_check_run");
                    }
                    catch (Exception ex) when (IsExpectedToolFailure(ex))
                    {
                        return BuildFailure("get_check_run", "check_run_request_invalid", ex.Message,
                            "Provide a checkRunId with the chk_ prefix.");
                    }

                    CheckRun? run = await database.CheckRuns.ReadAsync(request.CheckRunId).ConfigureAwait(false);
                    if (run == null)
                    {
                        return BuildFailure("get_check_run", "check_run_not_found", "Check run not found.",
                            "Verify the checkRunId with armada_enumerate entityType=checks or run_check to create a new check.",
                            checkRunId: request.CheckRunId);
                    }

                    // The complete log is returned only when it is asked for. A build or test log
                    // routinely runs to megabytes, which overruns the tool output limit and gives
                    // the caller a truncation error in place of the verdict it wanted.
                    if (request.IncludeOutput) return (object)run;
                    string? runPattern = await ReadFailedTestLinePatternAsync(database, run).ConfigureAwait(false);
                    return (object)CheckRunSummaryView.From(run, request.OutputTailLines ?? _DefaultOutputTailLines, HostWideCommandLock.Snapshot(), runPattern);
                });

            register(
                "run_check",
                "Start a structured check run for a vessel using the resolved workflow profile or an explicit command override. Returns status, exit code, parsed test totals, and the tail of the output; fetch the complete log with get_check_run includeOutput=true. A check still waiting for the host-wide build slot after two minutes is returned unfinished with its SlotWait; it keeps running, so read it again with get_check_run.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        vesselId = new { type = "string", description = "Target vessel ID (vsl_ prefix)" },
                        workflowProfileId = new { type = "string", description = "Optional workflow profile override (wfp_ prefix)" },
                        missionId = new { type = "string", description = "Optional linked mission ID (msn_ prefix)" },
                        voyageId = new { type = "string", description = "Optional linked voyage ID (vyg_ prefix)" },
                        type = new { type = "string", description = "Check type such as Build, UnitTest, IntegrationTest, Deploy, SmokeTest, HealthCheck, or ReleaseVersioning" },
                        environmentName = new { type = "string", description = "Optional workflow-profile environment name for deploy/rollback/verification checks" },
                        label = new { type = "string", description = "Optional display label override" },
                        branchName = new { type = "string", description = "Optional branch association" },
                        commitHash = new { type = "string", description = "Optional commit-hash association" },
                        regressionPurpose = new { type = "string", description = "Optional post-land regression class this Check guards: None, Consumer, or Ledger" },
                        regressionObjectiveId = new { type = "string", description = "Optional objective (obj_ prefix) whose landed change this Check guards" },
                        regressionLandedCommit = new { type = "string", description = "Optional landed commit hash this Check guards" },
                        commandOverride = new { type = "string", description = "Optional raw shell command to execute instead of the workflow-profile command. Global administrators only: it runs as the server process." }
                    },
                    required = new[] { "vesselId", "type" }
                },
                async (args) =>
                {
                    try
                    {
                        CheckRunRequest request = DeserializeArgs<CheckRunRequest>(args, "run_check");
                        AuthContext auth = McpCallerContext.Require();
                        // A check can wait a long time for the host-wide build slot. Waiting past a client's
                        // tool timeout reads to a captain as a failed check, so the call returns the unfinished
                        // record after a bounded wait; the check keeps running and finishes its own record.
                        CheckRun executed = await checkRunService.RunWithinAsync(auth, request, wait).ConfigureAwait(false);
                        string? executedPattern = await ReadFailedTestLinePatternAsync(database, executed).ConfigureAwait(false);
                        return (object)CheckRunSummaryView.From(executed, _DefaultOutputTailLines, HostWideCommandLock.Snapshot(), executedPattern);
                    }
                    catch (JsonException ex)
                    {
                        return BuildFailure("run_check", "check_run_request_invalid", ex.Message,
                            "Use a valid check type and include vesselId. Valid type values are returned with this response.",
                            validTypeValues: McpEnumArgument.ValidValues<CheckRunTypeEnum>());
                    }
                    catch (Exception ex) when (IsExpectedToolFailure(ex))
                    {
                        return BuildFailure("run_check", "check_run_failed", ex.Message,
                            "Verify vesselId, workflowProfileId, environmentName, and commandOverride; use armada_resolve_check when the command was executed externally.",
                            validTypeValues: McpEnumArgument.ValidValues<CheckRunTypeEnum>());
                    }
                });

            register(
                "retry_check_run",
                "Retry a structured check run using the same resolved scope and command context. A finished record is retried as a new record; a Pending record runs in place under its own ID, as an operator run that always executes its command and that the automatic runner neither starts nor discards meanwhile. Like run_check, it waits a bounded time: a retry still waiting for the host-wide slot returns unfinished with slotWait and keeps running. A retry runs to completion even when its voyage has ended.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        checkRunId = new { type = "string", description = "Check run ID (chk_ prefix)" }
                    },
                    required = new[] { "checkRunId" }
                },
                async (args) =>
                {
                    try
                    {
                        CheckRunIdArgs request = DeserializeArgs<CheckRunIdArgs>(args, "retry_check_run");
                        AuthContext auth = McpCallerContext.Require();
                        // Same bounded wait as run_check: a retry waiting for the host-wide slot returns the
                        // unfinished record with its slot wait, and finishes its own record in the background.
                        CheckRun retried = await checkRunService.RetryWithinAsync(auth, request.CheckRunId, wait).ConfigureAwait(false);
                        string? retriedPattern = await ReadFailedTestLinePatternAsync(database, retried).ConfigureAwait(false);
                        return (object)CheckRunSummaryView.From(retried, _DefaultOutputTailLines, HostWideCommandLock.Snapshot(), retriedPattern);
                    }
                    catch (Exception ex) when (IsExpectedToolFailure(ex))
                    {
                        return BuildFailure("retry_check_run", "check_run_retry_failed", ex.Message,
                            "Verify checkRunId with get_check_run or armada_enumerate entityType=checks; use run_check when no prior check exists.");
                    }
                });

            object resolveCheckSchema = new
            {
                type = "object",
                properties = new
                {
                    checkRunId = new { type = "string", description = "Check run ID (chk_ prefix)" },
                    status = new { type = "string", description = "Pending, Running, Passed, Failed, or Canceled" },
                    output = new { type = "string", description = "Optional output/details" },
                    summary = new { type = "string", description = "Optional human-readable summary" },
                    exitCode = new { type = "integer", description = "Optional exit code" },
                    includeOutput = new { type = "boolean", description = "Return the complete command log instead of a bounded tail. Defaults to false." },
                    outputTailLines = new { type = "integer", description = "How many trailing output lines to include when includeOutput is false. Defaults to 40." }
                },
                required = new[] { "checkRunId", "status" }
            };

            Func<JsonElement?, Task<object>> resolveCheckHandler = async (args) =>
            {
                CheckResolveArgs request;
                try
                {
                    request = DeserializeArgs<CheckResolveArgs>(args, "resolve_check");
                }
                catch (Exception ex) when (IsExpectedToolFailure(ex))
                {
                    return BuildFailure("resolve_check", "check_resolve_request_invalid", ex.Message,
                        "Provide checkRunId and a valid status.",
                        validStatusValues: McpEnumArgument.ValidValues<CheckRunStatusEnum>());
                }

                if (!Enum.TryParse(request.Status, true, out CheckRunStatusEnum status))
                {
                    return BuildFailure("resolve_check", "check_status_invalid", "Invalid status: " + request.Status,
                        "Use one of the ValidStatusValues returned with this response.",
                        checkRunId: request.CheckRunId,
                        validStatusValues: McpEnumArgument.ValidValues<CheckRunStatusEnum>());
                }

                CheckRun? run = await checkRunService.ResolveStatusManuallyAsync(
                    request.CheckRunId, status, request.Output, request.Summary, request.ExitCode).ConfigureAwait(false);
                if (run == null)
                {
                    return BuildFailure("resolve_check", "check_run_not_found", "Check run not found.",
                        "Verify the checkRunId with armada_enumerate entityType=checks or run_check to create a new check.",
                        checkRunId: request.CheckRunId);
                }

                // The resolved record carries the check's complete command log, which for a build or test
                // run is routinely megabytes and overruns the tool output limit, so the caller gets a
                // truncation error in place of the record it just resolved. The bounded view is returned by
                // default, exactly as get_check_run does; the complete log is returned only when asked for.
                if (request.IncludeOutput) return (object)run;
                string? runPattern = await ReadFailedTestLinePatternAsync(database, run).ConfigureAwait(false);
                return (object)CheckRunSummaryView.From(run, request.OutputTailLines ?? _DefaultOutputTailLines, HostWideCommandLock.Snapshot(), runPattern);
            };

            // resolve_check is the family-consistent name (matches run_check / get_check_run / retry_check_run).
            // armada_resolve_check is kept as a back-compat alias (2026-07-26 prefix normalization).
            register("resolve_check", "Update a structured check run status and optional output (no re-execution, unlike run_check/retry_check_run). Returns the bounded view (status, exit code, parsed totals, and the tail of the log); pass includeOutput=true for the complete log, which for a build or test run is routinely megabytes and can exceed the tool output limit.", resolveCheckSchema, resolveCheckHandler);
            register("armada_resolve_check", "Alias of resolve_check (back-compat).", resolveCheckSchema, resolveCheckHandler);
        }

        /// <summary>
        /// Read the failing-test line pattern of the workflow profile a check run recorded, so the bounded view
        /// names failing tests in the runner's own line shape. A run with no recorded profile, or whose profile
        /// is gone or sets no pattern, yields null and the view reads the output with the built-in matchers.
        /// </summary>
        private static async Task<string?> ReadFailedTestLinePatternAsync(DatabaseDriver database, CheckRun run)
        {
            if (String.IsNullOrWhiteSpace(run.WorkflowProfileId)) return null;
            WorkflowProfile? profile = await database.WorkflowProfiles.ReadAsync(run.WorkflowProfileId!).ConfigureAwait(false);
            return profile?.FailedTestLinePattern;
        }

        private static T DeserializeArgs<T>(JsonElement? args, string toolName)
        {
            if (!args.HasValue || args.Value.ValueKind == JsonValueKind.Null)
                throw new InvalidOperationException(toolName + " requires arguments.");

            return JsonSerializer.Deserialize<T>(args.Value, _JsonOptions)
                ?? throw new InvalidOperationException("Could not deserialize arguments for " + toolName + ".");
        }

        private static bool IsExpectedToolFailure(Exception ex)
        {
            return ex is JsonException || ex is InvalidOperationException || ex is ArgumentException || ex is UnauthorizedAccessException;
        }

        private static object BuildFailure(
            string tool,
            string code,
            string message,
            string action,
            string? checkRunId = null,
            string[]? validStatusValues = null,
            string[]? validTypeValues = null)
        {
            return new CheckRunToolFailure
            {
                Tool = tool,
                Code = code,
                Message = message,
                Action = action,
                CheckRunId = checkRunId,
                ValidStatusValues = validStatusValues,
                ValidTypeValues = validTypeValues
            };
        }

        private sealed class CheckResolveArgs
        {
            public string CheckRunId { get; set; } = "";

            public string Status { get; set; } = "";

            public string? Output { get; set; } = null;

            public string? Summary { get; set; } = null;

            public int? ExitCode { get; set; } = null;

            public bool IncludeOutput { get; set; } = false;

            public int? OutputTailLines { get; set; } = null;
        }

        private sealed class CheckRunToolFailure
        {
            public string Status { get; set; } = "failed";

            public string Tool { get; set; } = "";

            public string Code { get; set; } = "";

            public string Message { get; set; } = "";

            public string Action { get; set; } = "";

            public string? CheckRunId { get; set; } = null;

            public string[]? ValidStatusValues { get; set; } = null;

            public string[]? ValidTypeValues { get; set; } = null;
        }
    }
}
