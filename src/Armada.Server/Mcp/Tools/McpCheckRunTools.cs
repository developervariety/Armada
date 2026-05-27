namespace Armada.Server.Mcp.Tools
{
    using System;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;

    /// <summary>
    /// Registers MCP tools for structured check-run execution and inspection.
    /// </summary>
    public static class McpCheckRunTools
    {
        private static readonly JsonSerializerOptions _JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter() }
        };

        /// <summary>
        /// Registers structured check-run MCP tools.
        /// </summary>
        public static void Register(RegisterToolDelegate register, DatabaseDriver database, CheckRunService checkRunService)
        {
            register(
                "get_check_run",
                "Inspect one structured check run including status, output, artifacts, parsed test summary, and coverage summary.",
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

                    return (object)run;
                });

            register(
                "run_check",
                "Start a structured check run for a vessel using the resolved workflow profile or an explicit command override.",
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
                        commandOverride = new { type = "string", description = "Optional raw shell command to execute instead of the workflow-profile command" }
                    },
                    required = new[] { "vesselId", "type" }
                },
                async (args) =>
                {
                    try
                    {
                        CheckRunRequest request = DeserializeArgs<CheckRunRequest>(args, "run_check");
                        AuthContext auth = McpToolHelpers.CreateDefaultTenantAdminContext();
                        return (object)await checkRunService.RunAsync(auth, request).ConfigureAwait(false);
                    }
                    catch (JsonException ex)
                    {
                        return BuildFailure("run_check", "check_run_request_invalid", ex.Message,
                            "Use a valid check type and include vesselId. Valid type values are returned with this response.",
                            validTypeValues: Enum.GetNames<CheckRunTypeEnum>());
                    }
                    catch (Exception ex) when (IsExpectedToolFailure(ex))
                    {
                        return BuildFailure("run_check", "check_run_failed", ex.Message,
                            "Verify vesselId, workflowProfileId, environmentName, and commandOverride; use armada_resolve_check when the command was executed externally.",
                            validTypeValues: Enum.GetNames<CheckRunTypeEnum>());
                    }
                });

            register(
                "retry_check_run",
                "Retry a previously completed structured check run using the same resolved scope and command context.",
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
                        AuthContext auth = McpToolHelpers.CreateDefaultTenantAdminContext();
                        return (object)await checkRunService.RetryAsync(auth, request.CheckRunId).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (IsExpectedToolFailure(ex))
                    {
                        return BuildFailure("retry_check_run", "check_run_retry_failed", ex.Message,
                            "Verify checkRunId with get_check_run or armada_enumerate entityType=checks; use run_check when no prior check exists.");
                    }
                });

            register(
                "armada_resolve_check",
                "Update a structured check run status and optional output without dropping to REST.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        checkRunId = new { type = "string", description = "Check run ID (chk_ prefix)" },
                        status = new { type = "string", description = "Pending, Running, Passed, Failed, or Canceled" },
                        output = new { type = "string", description = "Optional output/details" },
                        summary = new { type = "string", description = "Optional human-readable summary" },
                        exitCode = new { type = "integer", description = "Optional exit code" }
                    },
                    required = new[] { "checkRunId", "status" }
                },
                async (args) =>
                {
                    CheckResolveArgs request;
                    try
                    {
                        request = DeserializeArgs<CheckResolveArgs>(args, "armada_resolve_check");
                    }
                    catch (Exception ex) when (IsExpectedToolFailure(ex))
                    {
                        return BuildFailure("armada_resolve_check", "check_resolve_request_invalid", ex.Message,
                            "Provide checkRunId and a valid status.",
                            validStatusValues: Enum.GetNames<CheckRunStatusEnum>());
                    }

                    if (!Enum.TryParse(request.Status, true, out CheckRunStatusEnum status))
                    {
                        return BuildFailure("armada_resolve_check", "check_status_invalid", "Invalid status: " + request.Status,
                            "Use one of the ValidStatusValues returned with this response.",
                            checkRunId: request.CheckRunId,
                            validStatusValues: Enum.GetNames<CheckRunStatusEnum>());
                    }

                    CheckRun? run = await database.CheckRuns.ReadAsync(request.CheckRunId).ConfigureAwait(false);
                    if (run == null)
                    {
                        return BuildFailure("armada_resolve_check", "check_run_not_found", "Check run not found.",
                            "Verify the checkRunId with armada_enumerate entityType=checks or run_check to create a new check.",
                            checkRunId: request.CheckRunId);
                    }

                    run.Status = status;
                    if (request.Output != null) run.Output = request.Output;
                    if (request.Summary != null) run.Summary = request.Summary;
                    if (request.ExitCode.HasValue) run.ExitCode = request.ExitCode.Value;
                    if (status == CheckRunStatusEnum.Running && run.StartedUtc == null) run.StartedUtc = DateTime.UtcNow;
                    if (status == CheckRunStatusEnum.Passed || status == CheckRunStatusEnum.Failed || status == CheckRunStatusEnum.Canceled)
                    {
                        run.CompletedUtc ??= DateTime.UtcNow;
                    }
                    run.LastUpdateUtc = DateTime.UtcNow;
                    run = await database.CheckRuns.UpdateAsync(run).ConfigureAwait(false);
                    checkRunService.OnCheckRunChanged?.Invoke(run);
                    return (object)run;
                });
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
            return ex is JsonException || ex is InvalidOperationException || ex is ArgumentException;
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
