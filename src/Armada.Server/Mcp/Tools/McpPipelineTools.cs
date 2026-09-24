namespace Armada.Server.Mcp.Tools
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Armada.Core;
    using ArmadaConstants = Armada.Core.Constants;
    using Armada.Core.Database;
    using Armada.Core.Models;
    using Armada.Core.Services;

    /// <summary>
    /// Registers MCP tools for pipeline CRUD operations.
    /// </summary>
    public static class McpPipelineTools
    {
        private static readonly JsonSerializerOptions _JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
        };

        /// <summary>
        /// Registers pipeline MCP tools with the server.
        /// </summary>
        /// <param name="register">Delegate to register each tool.</param>
        /// <param name="database">Database driver for pipeline data access.</param>
        public static void Register(RegisterToolDelegate register, DatabaseDriver database)
        {
            PipelineService pipelines = new PipelineService(database);

            register(
                "create_pipeline",
                "Create a new pipeline with an ordered sequence of persona stages. Without explicit orders the stages run in list order; stages that share an order run as parallel siblings.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        name = new { type = "string", description = "Pipeline name (e.g. 'WorkerOnly', 'FullPipeline', 'Reviewed'). A name already used in the caller's tenant is refused with code conflict." },
                        description = new { type = "string", description = "Human-readable description of the pipeline workflow" },
                        active = new { type = "boolean", description = "Whether the pipeline is active (default true)" },
                        ownershipScope = OwnershipScopeSchema(),
                        stages = StagesSchema("Ordered list of pipeline stages; must not be empty")
                    },
                    required = new[] { "name", "stages" }
                },
                async (args) =>
                {
                    PipelineWriteRequest request = JsonSerializer.Deserialize<PipelineWriteRequest>(args!.Value, _JsonOptions) ?? new PipelineWriteRequest();
                    RecordWriteResult<Pipeline> result = await pipelines.CreateAsync(McpCallerContext.Require(), request).ConfigureAwait(false);
                    return McpRecordWriteResult.From(result);
                });

            register(
                "get_pipeline",
                "Get a pipeline by name, including its stages",
                new
                {
                    type = "object",
                    properties = new
                    {
                        name = new { type = "string", description = "Pipeline name" }
                    },
                    required = new[] { "name" }
                },
                async (args) =>
                {
                    PipelineWriteRequest request = JsonSerializer.Deserialize<PipelineWriteRequest>(args!.Value, _JsonOptions) ?? new PipelineWriteRequest();
                    string name = request.Name ?? "";
                    if (String.IsNullOrEmpty(name)) return (object)new { Error = "name is required" };
                    Pipeline? pipeline = await pipelines.ReadVisibleAsync(McpCallerContext.Require(), name).ConfigureAwait(false);
                    if (pipeline == null) return (object)new { Error = "Pipeline not found: " + name };
                    return (object)pipeline;
                });

            register(
                "update_pipeline",
                "Update an existing pipeline. Only supplied fields change. A non-empty stages list replaces the stages; an empty list is refused. A stage field left out keeps the value of the existing stage for the same persona, so an update that does not name requiresReview keeps the review gate.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        name = new { type = "string", description = "Pipeline name (used to look up the pipeline)" },
                        description = new { type = "string", description = "New description" },
                        active = new { type = "boolean", description = "Whether the pipeline is active" },
                        stages = StagesSchema("New ordered list of pipeline stages (replaces existing stages); must not be empty")
                    },
                    required = new[] { "name" }
                },
                async (args) =>
                {
                    PipelineWriteRequest request = JsonSerializer.Deserialize<PipelineWriteRequest>(args!.Value, _JsonOptions) ?? new PipelineWriteRequest();
                    // The name addresses the record; an update never takes ownership scope from the request.
                    request.OwnershipScope = null;
                    RecordWriteResult<Pipeline> result = await pipelines.UpdateAsync(McpCallerContext.Require(), request.Name, request).ConfigureAwait(false);
                    return McpRecordWriteResult.From(result);
                });

            register(
                "delete_pipeline",
                "Delete a pipeline by name. Built-in pipelines cannot be deleted.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        name = new { type = "string", description = "Pipeline name" }
                    },
                    required = new[] { "name" }
                },
                async (args) =>
                {
                    PipelineWriteRequest request = JsonSerializer.Deserialize<PipelineWriteRequest>(args!.Value, _JsonOptions) ?? new PipelineWriteRequest();
                    RecordWriteResult<Pipeline> result = await pipelines.DeleteAsync(McpCallerContext.Require(), request.Name).ConfigureAwait(false);
                    if (!result.Succeeded) return McpRecordWriteResult.From(result);
                    return (object)new { Status = "deleted", Name = result.Record!.Name };
                });
        }

        private static object OwnershipScopeSchema()
        {
            return new { type = "string", @enum = new[] { "TenantWide", "UserSpecific" }, description = "Who may see the record inside its tenant. An administrator's choice is kept (TenantWide when omitted); any other caller's record is UserSpecific." };
        }

        private static object StagesSchema(string description)
        {
            return new
            {
                type = "array",
                description = description,
                items = new
                {
                    type = "object",
                    properties = new
                    {
                        personaName = new { type = "string", description = "Persona name for this stage" },
                        order = new { type = "integer", description = "Execution order. Give every stage an order (stages that share one run as parallel siblings) or none, and list position numbers the stages 1..n." },
                        isOptional = new { type = "boolean", description = "Whether this stage is optional (default false)" },
                        requiresReview = new { type = "boolean", description = "Whether this stage requires an explicit review approval before the pipeline continues (default false)" },
                        reviewDenyAction = new { type = "string", @enum = new[] { "RetryStage", "FailPipeline" }, description = "Action when the review is denied (default RetryStage)" },
                        description = new { type = "string", description = "Description of what this stage does" },
                        preferredModel = new { type = "string", description = "Optional per-stage complexity tier: 'low', 'mid', or 'high'. When set, this stage uses that tier instead of the per-mission preferredModel. Null means inherit the dispatch's preferredModel." }
                    },
                    required = new[] { "personaName" }
                }
            };
        }
    }
}
