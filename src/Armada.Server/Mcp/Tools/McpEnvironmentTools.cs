namespace Armada.Server.Mcp.Tools
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using Armada.Core.Models;
    using Armada.Core.Services;

    /// <summary>
    /// Registers operator-only MCP tools for deployment environments.
    /// </summary>
    public static class McpEnvironmentTools
    {
        private static readonly JsonSerializerOptions _JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter() }
        };

        /// <summary>
        /// Register environment tools.
        /// </summary>
        public static void Register(RegisterToolDelegate register, DeploymentEnvironmentService environments)
        {
            register(
                "list_environments",
                "List deployment environments. This is operator-owned state; a captain may use it only when its mission explicitly assigns that operator action.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        vesselId = new { type = "string" },
                        kind = new { type = "string", description = "Development, Staging, Production, or Other" },
                        isDefault = new { type = "boolean" },
                        active = new { type = "boolean" },
                        search = new { type = "string" },
                        pageNumber = new { type = "integer" },
                        pageSize = new { type = "integer" }
                    }
                },
                async args =>
                {
                    DeploymentEnvironmentQuery query = Deserialize<DeploymentEnvironmentQuery>(args) ?? new DeploymentEnvironmentQuery();
                    query.PageNumber = Math.Max(1, query.PageNumber);
                    query.PageSize = Math.Clamp(query.PageSize, 1, 500);
                    AuthContext auth = McpToolHelpers.CreateDefaultTenantAdminContext();
                    return (object)await environments.EnumerateAsync(auth, query).ConfigureAwait(false);
                });

            register(
                "get_environment",
                "Get one deployment environment by ID.",
                IdSchema(),
                async args =>
                {
                    string id = RequiredString(args, "environmentId");
                    AuthContext auth = McpToolHelpers.CreateDefaultTenantAdminContext();
                    return (object?)await environments.ReadAsync(auth, id).ConfigureAwait(false)
                        ?? new { Error = "Environment not found" };
                });

            register(
                "create_environment",
                "Create a deployment environment from a complete environment request.",
                EnvironmentSchema(requireId: false),
                async args =>
                {
                    DeploymentEnvironmentUpsertRequest request = ReadRequest(args);
                    AuthContext auth = McpToolHelpers.CreateDefaultTenantAdminContext();
                    return (object)await environments.CreateAsync(auth, request).ConfigureAwait(false);
                });

            register(
                "update_environment",
                "Replace a deployment environment record. Send every text field that must be retained; an omitted nullable text field is cleared by service replacement semantics.",
                EnvironmentSchema(requireId: true),
                async args =>
                {
                    string id = RequiredString(args, "environmentId");
                    DeploymentEnvironmentUpsertRequest request = ReadRequest(args);
                    AuthContext auth = McpToolHelpers.CreateDefaultTenantAdminContext();
                    return (object)await environments.UpdateAsync(auth, id, request).ConfigureAwait(false);
                });

            register(
                "delete_environment",
                "Delete one deployment environment by ID.",
                IdSchema(),
                async args =>
                {
                    string id = RequiredString(args, "environmentId");
                    AuthContext auth = McpToolHelpers.CreateDefaultTenantAdminContext();
                    await environments.DeleteAsync(auth, id).ConfigureAwait(false);
                    return (object)new { Status = "deleted", EnvironmentId = id };
                });
        }

        private static object IdSchema()
        {
            return new
            {
                type = "object",
                properties = new
                {
                    environmentId = new { type = "string", description = "Environment ID (env_ prefix)" }
                },
                required = new[] { "environmentId" }
            };
        }

        private static object EnvironmentSchema(bool requireId)
        {
            Dictionary<string, object> properties = new Dictionary<string, object>
            {
                ["environment"] = new
                {
                    type = "object",
                    description = "DeploymentEnvironmentUpsertRequest JSON",
                    additionalProperties = true
                }
            };
            if (requireId)
                properties["environmentId"] = new { type = "string", description = "Environment ID (env_ prefix)" };

            return new
            {
                type = "object",
                properties,
                required = requireId ? new[] { "environmentId", "environment" } : new[] { "environment" }
            };
        }

        private static DeploymentEnvironmentUpsertRequest ReadRequest(JsonElement? args)
        {
            if (!args.HasValue || !args.Value.TryGetProperty("environment", out JsonElement element))
                throw new InvalidOperationException("environment is required");
            return JsonSerializer.Deserialize<DeploymentEnvironmentUpsertRequest>(element.GetRawText(), _JsonOptions)
                ?? throw new InvalidOperationException("Could not deserialize DeploymentEnvironmentUpsertRequest.");
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
