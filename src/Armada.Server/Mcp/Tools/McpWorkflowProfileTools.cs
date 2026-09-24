namespace Armada.Server.Mcp.Tools
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;

    /// <summary>
    /// Registers operator-only MCP tools for workflow-profile administration.
    /// </summary>
    public static class McpWorkflowProfileTools
    {
        private static readonly JsonSerializerOptions _JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter() }
        };

        /// <summary>
        /// Register workflow-profile tools.
        /// </summary>
        public static void Register(
            RegisterToolDelegate register,
            DatabaseDriver database,
            WorkflowProfileService workflowProfiles)
        {
            register(
                "list_workflow_profiles",
                "List workflow profiles. This is operator-owned state; a captain may use it only when its mission explicitly assigns that operator action.",
                QuerySchema(),
                async args =>
                {
                    WorkflowProfileQuery query = DeserializeOrDefault<WorkflowProfileQuery>(args) ?? new WorkflowProfileQuery();
                    query.PageNumber = Math.Max(1, query.PageNumber);
                    query.PageSize = Math.Clamp(query.PageSize, 1, 500);
                    return (object)await database.WorkflowProfiles.EnumerateAsync(query).ConfigureAwait(false);
                });

            register(
                "get_workflow_profile",
                "Get one workflow profile by ID.",
                IdSchema("workflowProfileId", "Workflow profile ID (wfp_ prefix)"),
                async args =>
                {
                    string id = RequiredString(args, "workflowProfileId");
                    WorkflowProfile? profile = await workflowProfiles.ReadForCallerAsync(McpCallerContext.Require(), id).ConfigureAwait(false);
                    return profile ?? (object)new { Error = "Workflow profile not found", Code = "not_found" };
                });

            register(
                "validate_workflow_profile",
                "Validate a complete workflow profile without saving it, exactly as create_workflow_profile would store it for the caller.",
                ProfileSchema(requireId: false),
                async args =>
                {
                    WorkflowProfile? profile = TryReadProfile(args, out object? refusal);
                    if (profile == null) return refusal!;
                    return (object)await workflowProfiles.ValidateForCallerAsync(McpCallerContext.Require(), profile).ConfigureAwait(false);
                });

            register(
                "preview_workflow_profile",
                "Preview the workflow profile and commands that resolve for a vessel.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        vesselId = new { type = "string", description = "Vessel ID (vsl_ prefix)" },
                        workflowProfileId = new { type = "string", description = "Optional explicit workflow profile ID" }
                    },
                    required = new[] { "vesselId" }
                },
                async args =>
                {
                    string vesselId = RequiredString(args, "vesselId");
                    string? profileId = OptionalString(args, "workflowProfileId");
                    Vessel? vessel = await database.Vessels.ReadAsync(vesselId).ConfigureAwait(false);
                    if (vessel == null) return (object)new { Error = "Vessel not found" };
                    AuthContext auth = McpCallerContext.Require();
                    return (object?)await workflowProfiles.PreviewForVesselAsync(auth, vessel, profileId).ConfigureAwait(false)
                        ?? new { Error = "No workflow profile resolves for this vessel" };
                });

            register(
                "create_workflow_profile",
                "Create a validated workflow profile. The profile property must contain the complete record.",
                ProfileSchema(requireId: false),
                async args =>
                {
                    WorkflowProfile? profile = TryReadProfile(args, out object? refusal);
                    if (profile == null) return refusal!;
                    return McpRecordWriteResult.From(await workflowProfiles.CreateAsync(McpCallerContext.Require(), profile).ConfigureAwait(false));
                });

            register(
                "update_workflow_profile",
                "Replace a workflow profile with a validated complete record, including environmentVariables. Identity, owner, tenant and creation time are kept.",
                ProfileSchema(requireId: true),
                async args =>
                {
                    string id = RequiredString(args, "workflowProfileId");
                    WorkflowProfile? profile = TryReadProfile(args, out object? refusal);
                    if (profile == null) return refusal!;
                    return McpRecordWriteResult.From(await workflowProfiles.ReplaceAsync(McpCallerContext.Require(), id, profile).ConfigureAwait(false));
                });

            register(
                "delete_workflow_profile",
                "Delete one workflow profile by ID.",
                IdSchema("workflowProfileId", "Workflow profile ID (wfp_ prefix)"),
                async args =>
                {
                    string id = RequiredString(args, "workflowProfileId");
                    RecordWriteResult<WorkflowProfile> result = await workflowProfiles.DeleteAsync(McpCallerContext.Require(), id).ConfigureAwait(false);
                    if (!result.Succeeded) return McpRecordWriteResult.From(result);
                    return (object)new { Status = "deleted", WorkflowProfileId = result.Record!.Id };
                });
        }

        private static object QuerySchema()
        {
            return new
            {
                type = "object",
                properties = new
                {
                    scope = new { type = "string", description = "Optional Global, Fleet, or Vessel scope" },
                    fleetId = new { type = "string" },
                    vesselId = new { type = "string" },
                    search = new { type = "string" },
                    active = new { type = "boolean" },
                    pageNumber = new { type = "integer" },
                    pageSize = new { type = "integer" }
                }
            };
        }

        private static object ProfileSchema(bool requireId)
        {
            Dictionary<string, object> properties = new Dictionary<string, object>
            {
                ["profile"] = new
                {
                    type = "object",
                    description = "Complete WorkflowProfile JSON record",
                    additionalProperties = true
                }
            };
            if (requireId)
                properties["workflowProfileId"] = new { type = "string", description = "Workflow profile ID (wfp_ prefix)" };

            return new
            {
                type = "object",
                properties,
                required = requireId ? new[] { "workflowProfileId", "profile" } : new[] { "profile" }
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

        private static WorkflowProfile? TryReadProfile(JsonElement? args, out object? refusal)
        {
            refusal = null;
            try
            {
                return ReadProfile(args);
            }
            catch (Exception ex) when (ex is JsonException || ex is ArgumentException || ex is InvalidOperationException)
            {
                // A model setter that refuses a value (such as a blank required name) throws while the profile is
                // read; the caller gets the same invalid-request refusal REST returns.
                refusal = new { Error = "profile is not valid: " + ex.Message, Code = "invalid" };
                return null;
            }
        }

        private static WorkflowProfile ReadProfile(JsonElement? args)
        {
            if (!args.HasValue || !args.Value.TryGetProperty("profile", out JsonElement profileElement))
                throw new InvalidOperationException("profile is required");
            return JsonSerializer.Deserialize<WorkflowProfile>(profileElement.GetRawText(), _JsonOptions)
                ?? throw new InvalidOperationException("Could not deserialize WorkflowProfile.");
        }

        private static T? DeserializeOrDefault<T>(JsonElement? args)
        {
            if (!args.HasValue || args.Value.ValueKind == JsonValueKind.Null)
                return default;
            return JsonSerializer.Deserialize<T>(args.Value.GetRawText(), _JsonOptions);
        }

        private static string RequiredString(JsonElement? args, string name)
        {
            return OptionalString(args, name) ?? throw new InvalidOperationException(name + " is required");
        }

        private static string? OptionalString(JsonElement? args, string name)
        {
            if (!args.HasValue || !args.Value.TryGetProperty(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
                return null;
            string? result = value.GetString();
            return String.IsNullOrWhiteSpace(result) ? null : result.Trim();
        }
    }
}
