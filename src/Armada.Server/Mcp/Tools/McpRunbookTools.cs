namespace Armada.Server.Mcp.Tools
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using Armada.Core.Models;
    using Armada.Core.Services;

    /// <summary>
    /// Registers MCP tools for runbook inspection and execution.
    /// </summary>
    public static class McpRunbookTools
    {
        private static readonly JsonSerializerOptions _JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter() }
        };

        /// <summary>
        /// Registers runbook MCP tools.
        /// </summary>
        public static void Register(RegisterToolDelegate register, RunbookService runbookService)
        {
            register(
                "list_runbooks",
                "List playbook-backed runbooks. This is operator-owned state; a captain may use it only when its mission explicitly assigns that operator action.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        workflowProfileId = new { type = "string" },
                        environmentId = new { type = "string" },
                        defaultCheckType = new { type = "string" },
                        active = new { type = "boolean" },
                        search = new { type = "string" },
                        pageNumber = new { type = "integer" },
                        pageSize = new { type = "integer" }
                    }
                },
                async args =>
                {
                    RunbookQuery query = Deserialize<RunbookQuery>(args) ?? new RunbookQuery();
                    AuthContext auth = McpToolHelpers.CreateDefaultTenantAdminContext();
                    return (object)await runbookService.EnumerateAsync(auth, query).ConfigureAwait(false);
                });

            register(
                "create_runbook",
                "Create a playbook-backed runbook with explicit parameters, steps, and evidence instructions.",
                RunbookWriteSchema(requireId: false),
                async args =>
                {
                    RunbookUpsertRequest request = ReadNested<RunbookUpsertRequest>(args, "runbook");
                    AuthContext auth = McpToolHelpers.CreateDefaultTenantAdminContext();
                    return (object)await runbookService.CreateAsync(auth, request).ConfigureAwait(false);
                });

            register(
                "update_runbook",
                "Update a playbook-backed runbook with a complete runbook request.",
                RunbookWriteSchema(requireId: true),
                async args =>
                {
                    string id = RequiredString(args, "runbookId");
                    RunbookUpsertRequest request = ReadNested<RunbookUpsertRequest>(args, "runbook");
                    AuthContext auth = McpToolHelpers.CreateDefaultTenantAdminContext();
                    return (object)await runbookService.UpdateAsync(auth, id, request).ConfigureAwait(false);
                });

            register(
                "delete_runbook",
                "Delete one playbook-backed runbook.",
                IdSchema("runbookId", "Runbook ID (same as playbook ID)"),
                async args =>
                {
                    string id = RequiredString(args, "runbookId");
                    AuthContext auth = McpToolHelpers.CreateDefaultTenantAdminContext();
                    await runbookService.DeleteAsync(auth, id).ConfigureAwait(false);
                    return (object)new { Status = "deleted", RunbookId = id };
                });

            register(
                "list_runbook_executions",
                "List runbook execution records and their current status.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        runbookId = new { type = "string" },
                        deploymentId = new { type = "string" },
                        incidentId = new { type = "string" },
                        status = new { type = "string" },
                        search = new { type = "string" },
                        pageNumber = new { type = "integer" },
                        pageSize = new { type = "integer" }
                    }
                },
                async args =>
                {
                    RunbookExecutionQuery query = Deserialize<RunbookExecutionQuery>(args) ?? new RunbookExecutionQuery();
                    AuthContext auth = McpToolHelpers.CreateDefaultTenantAdminContext();
                    return (object)await runbookService.EnumerateExecutionsAsync(auth, query).ConfigureAwait(false);
                });

            register(
                "update_runbook_execution",
                "Record completed steps, notes, or a terminal status on a runbook execution.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        runbookExecutionId = new { type = "string", description = "Runbook execution ID (rbx_ prefix)" },
                        update = new { type = "object", description = "RunbookExecutionUpdateRequest JSON", additionalProperties = true }
                    },
                    required = new[] { "runbookExecutionId", "update" }
                },
                async args =>
                {
                    string id = RequiredString(args, "runbookExecutionId");
                    RunbookExecutionUpdateRequest request = ReadNested<RunbookExecutionUpdateRequest>(args, "update");
                    AuthContext auth = McpToolHelpers.CreateDefaultTenantAdminContext();
                    return (object)await runbookService.UpdateExecutionAsync(auth, id, request).ConfigureAwait(false);
                });

            register(
                "delete_runbook_execution",
                "Delete one runbook execution and its event snapshots.",
                IdSchema("runbookExecutionId", "Runbook execution ID (rbx_ prefix)"),
                async args =>
                {
                    string id = RequiredString(args, "runbookExecutionId");
                    AuthContext auth = McpToolHelpers.CreateDefaultTenantAdminContext();
                    await runbookService.DeleteExecutionAsync(auth, id).ConfigureAwait(false);
                    return (object)new { Status = "deleted", RunbookExecutionId = id };
                });

            register(
                "get_runbook",
                "Inspect one runbook including parameters, bound workflow profile, environment, and step structure.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        runbookId = new { type = "string", description = "Runbook ID (same as playbook ID)" }
                    },
                    required = new[] { "runbookId" }
                },
                async (args) =>
                {
                    RunbookIdArgs request = JsonSerializer.Deserialize<RunbookIdArgs>(args!.Value, _JsonOptions)
                        ?? throw new InvalidOperationException("Could not deserialize RunbookIdArgs.");
                    AuthContext auth = McpToolHelpers.CreateDefaultTenantAdminContext();
                    Runbook? runbook = await runbookService.ReadAsync(auth, request.RunbookId).ConfigureAwait(false);
                    if (runbook == null) return (object)new { Error = "Runbook not found" };
                    return (object)runbook;
                });

            register(
                "get_runbook_execution",
                "Inspect one runbook execution including completed steps, notes, and deployment or incident linkage.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        runbookExecutionId = new { type = "string", description = "Runbook execution ID (rbx_ prefix)" }
                    },
                    required = new[] { "runbookExecutionId" }
                },
                async (args) =>
                {
                    RunbookExecutionIdArgs request = JsonSerializer.Deserialize<RunbookExecutionIdArgs>(args!.Value, _JsonOptions)
                        ?? throw new InvalidOperationException("Could not deserialize RunbookExecutionIdArgs.");
                    AuthContext auth = McpToolHelpers.CreateDefaultTenantAdminContext();
                    RunbookExecution? execution = await runbookService.ReadExecutionAsync(auth, request.RunbookExecutionId).ConfigureAwait(false);
                    if (execution == null) return (object)new { Error = "Runbook execution not found" };
                    return (object)execution;
                });

            register(
                "start_runbook_execution",
                "Start a guided runbook execution with optional parameter overrides and deployment or incident context.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        runbookId = new { type = "string", description = "Runbook ID (same as playbook ID)" },
                        title = new { type = "string", description = "Optional execution title override" },
                        workflowProfileId = new { type = "string", description = "Optional workflow profile override (wfp_ prefix)" },
                        environmentId = new { type = "string", description = "Optional environment ID (env_ prefix)" },
                        environmentName = new { type = "string", description = "Optional environment name override" },
                        checkType = new { type = "string", description = "Optional check type override" },
                        deploymentId = new { type = "string", description = "Optional related deployment ID (dpl_ prefix)" },
                        incidentId = new { type = "string", description = "Optional related incident ID (inc_ prefix)" },
                        notes = new { type = "string", description = "Optional execution notes" },
                        parameterValues = new
                        {
                            type = "object",
                            additionalProperties = new { type = "string" },
                            description = "Optional parameter-value map"
                        }
                    },
                    required = new[] { "runbookId" }
                },
                async (args) =>
                {
                    JsonElement value = args!.Value;
                    string runbookId = value.GetProperty("runbookId").GetString() ?? String.Empty;
                    RunbookExecutionStartRequest request = JsonSerializer.Deserialize<RunbookExecutionStartRequest>(value, _JsonOptions)
                        ?? new RunbookExecutionStartRequest();
                    AuthContext auth = McpToolHelpers.CreateDefaultTenantAdminContext();
                    return (object)await runbookService.StartExecutionAsync(auth, runbookId, request).ConfigureAwait(false);
                });
        }

        private static object RunbookWriteSchema(bool requireId)
        {
            Dictionary<string, object> properties = new Dictionary<string, object>
            {
                ["runbook"] = new
                {
                    type = "object",
                    description = "RunbookUpsertRequest JSON",
                    additionalProperties = true
                }
            };
            if (requireId)
                properties["runbookId"] = new { type = "string", description = "Runbook ID (same as playbook ID)" };
            return new
            {
                type = "object",
                properties,
                required = requireId ? new[] { "runbookId", "runbook" } : new[] { "runbook" }
            };
        }

        private static object IdSchema(string name, string description)
        {
            return new
            {
                type = "object",
                properties = new Dictionary<string, object>
                {
                    [name] = new { type = "string", description }
                },
                required = new[] { name }
            };
        }

        private static T ReadNested<T>(JsonElement? args, string propertyName)
        {
            if (!args.HasValue || !args.Value.TryGetProperty(propertyName, out JsonElement element))
                throw new InvalidOperationException(propertyName + " is required");
            return JsonSerializer.Deserialize<T>(element.GetRawText(), _JsonOptions)
                ?? throw new InvalidOperationException("Could not deserialize " + typeof(T).Name + ".");
        }

        private static T? Deserialize<T>(JsonElement? args)
        {
            if (!args.HasValue || args.Value.ValueKind == JsonValueKind.Null)
                return default;
            return JsonSerializer.Deserialize<T>(args.Value.GetRawText(), _JsonOptions);
        }

        private static string RequiredString(JsonElement? args, string name)
        {
            if (!args.HasValue || !args.Value.TryGetProperty(name, out JsonElement value))
                throw new InvalidOperationException(name + " is required");
            string? result = value.GetString();
            if (String.IsNullOrWhiteSpace(result)) throw new InvalidOperationException(name + " is required");
            return result.Trim();
        }
    }
}
